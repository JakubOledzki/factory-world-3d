using System.Collections.Concurrent;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>How long a plot has stood still in all, and since when it stands still now (null while it works).</summary>
public sealed class PlotPauseRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public TimeSpan Paused { get; set; }

    public DateTimeOffset? PausedSince { get; set; }
}

/// <summary>When the plots were last checked against who is in the game: the server was up until then.</summary>
public sealed class PlotClockCheckRow
{
    public int Id { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
}

public sealed class PlotClock(GameDb db, TimeProvider time) : IPlotClock
{
    public async Task<DateTimeOffset> NowAsync(int x, int y, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var command = await db.CommandAsync(
            "SELECT paused, paused_since FROM world.plot_pauses WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return now;
        }

        var paused = reader.GetFieldValue<TimeSpan>(0);
        if (!reader.IsDBNull(1) && reader.GetFieldValue<DateTimeOffset>(1) is var since && since < now)
        {
            paused += now - since;
        }

        return now - paused;
    }

    // The plot is looked up here rather than through IPlotObjects: the plot services ask this clock themselves.
    public async Task<DateTimeOffset> NowOfAsync(Guid objectId, CancellationToken cancellationToken)
    {
        (int X, int Y)? plot = null;
        await using (var command = await db.CommandAsync("SELECT x, y FROM world.plot_objects WHERE id = @id", cancellationToken))
        {
            command.Parameters.AddWithValue("id", objectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                plot = (reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        return plot is { } at ? await NowAsync(at.X, at.Y, cancellationToken) : time.GetUtcNow();
    }
}

/// <summary>
/// Checks every <see cref="PlotClockRules.Check"/> which plots have a player in the game (their owner, or any member
/// of the company a plot is in, counting those who left less than <see cref="PlotClockRules.Grace"/> ago): a plot
/// without one starts standing still, one with a player back adds the stretch it stood still to its pause. When the
/// server starts, every plot stood still from the last check before it went down until its players come back.
/// With PlotClock:Auto set to false nothing runs by itself (the tests call <see cref="CheckAsync"/>).
/// </summary>
public sealed class PlotClockKeeper(
    IServiceScopeFactory scopes, OnlinePlayers online, TimeProvider time, IConfiguration configuration, ILogger<PlotClockKeeper> logger)
    : BackgroundService
{
    /// <summary>When each account was last seen in the game.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("PlotClock:Auto", true))
        {
            return;
        }

        try
        {
            await PauseAfterDowntimeAsync(stoppingToken);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Pausing the plots after the server was down failed");
        }

        using var timer = new PeriodicTimer(PlotClockRules.Check);
        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Checking which plots work failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>The plots stood still from the last check before the server went down.</summary>
    public async Task PauseAfterDowntimeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(
                """
                INSERT INTO world.plot_pauses (x, y, paused, paused_since)
                SELECT p.x, p.y, INTERVAL '0', COALESCE((SELECT checked_at FROM world.plot_clock WHERE id = 1), @now)
                FROM world.plots p
                ON CONFLICT (x, y) DO UPDATE SET paused_since = EXCLUDED.paused_since WHERE world.plot_pauses.paused_since IS NULL
                """, cancellationToken);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return 0;
        }, cancellationToken);
    }

    /// <summary>Starts or ends the pause of every plot whose players came or went.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var account in online.Online)
        {
            _seen[account] = now;
        }

        foreach (var gone in _seen.Where(s => now - s.Value >= PlotClockRules.Grace).Select(s => s.Key).ToList())
        {
            _seen.TryRemove(gone, out _);
        }

        var players = _seen.Keys.ToArray();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(
                """
                CREATE TEMP TABLE working ON COMMIT DROP AS
                SELECT p.x, p.y FROM world.plots p
                WHERE p.owner_id = ANY(@players)
                   OR EXISTS (SELECT 1 FROM companies.plots c JOIN companies.members m ON m.company_id = c.company_id
                              WHERE c.x = p.x AND c.y = p.y AND m.account_id = ANY(@players));
                UPDATE world.plot_pauses s SET paused = s.paused + GREATEST(@now - s.paused_since, INTERVAL '0'), paused_since = NULL
                FROM working w WHERE s.x = w.x AND s.y = w.y AND s.paused_since IS NOT NULL;
                INSERT INTO world.plot_pauses (x, y, paused, paused_since)
                SELECT p.x, p.y, INTERVAL '0', @now FROM world.plots p
                WHERE NOT EXISTS (SELECT 1 FROM working w WHERE w.x = p.x AND w.y = p.y)
                ON CONFLICT (x, y) DO UPDATE SET paused_since = @now WHERE world.plot_pauses.paused_since IS NULL;
                INSERT INTO world.plot_clock (id, checked_at) VALUES (1, @now)
                ON CONFLICT (id) DO UPDATE SET checked_at = @now;
                """, cancellationToken);
            command.Parameters.AddWithValue("players", players);
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return 0;
        }, cancellationToken);
    }
}
