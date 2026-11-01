using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Reports;

/// <summary>When a player was last seen, and the time they were away last, until they read its report.</summary>
public sealed class PresenceRow
{
    public Guid OwnerId { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? AwayFrom { get; set; }

    public DateTimeOffset? AwayTo { get; set; }
}

public sealed class ReportsDbContext(DbContextOptions<ReportsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "reports";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<PresenceRow>(e =>
        {
            e.ToTable("presence");
            e.HasKey(p => p.OwnerId);
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(p => p.AwayFrom).HasColumnName("away_from");
            e.Property(p => p.AwayTo).HasColumnName("away_to");
        });
    }
}

public static class AwayRules
{
    /// <summary>A player away this long gets a report when they come back.</summary>
    public static readonly TimeSpan Away = TimeSpan.FromMinutes(30);

    /// <summary>Being seen is written down at most this often per connection.</summary>
    public static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(1);

    /// <summary>The report lists at most this many items made.</summary>
    public const int MadeShown = 8;

    /// <summary>Ledger reasons (as the modules write them) by their start, and the group the report shows them under.</summary>
    private static readonly (string Start, string Group)[] Groups =
    [
        ("export", "Eksport koleją"),
        ("sell ", "Sprzedaż handlarzowi"),
        ("fence sell", "Paser"),
        ("filled order", "Rynek graczy"),
        ("sold ", "Rynek graczy"),
        ("market ", "Rynek graczy"),
        ("buy ", "Zakupy u handlarza"),
        ("takings", "Utarg sklepów i atrakcji"),
        ("power", "Prąd"),
        ("bank", "Banki"),
        ("rail route", "Kolej"),
        ("plot tax", "Podatek od działek"),
        ("service", "Przeglądy maszyn i usługi"),
        ("town ", "Dostawy do miast"),
        ("tender", "Przetargi miast"),
        ("subsidy", "Dopłaty państwa"),
        ("company", "Spółka"),
        ("challenge", "Rywalizacja"),
        ("help ", "Pomoc botom"),
        ("police", "Policja"),
    ];

    /// <summary>The group a ledger reason goes into, e.g. "Eksport koleją".</summary>
    public static string GroupOf(string reason)
    {
        foreach (var (start, group) in Groups)
        {
            if (reason.StartsWith(start, StringComparison.Ordinal))
            {
                return group;
            }
        }

        return reason.Contains(", fee ", StringComparison.Ordinal) ? "Rynek graczy" : "Inne";
    }
}

/// <summary>
/// Notes when each player is seen: the first request of a connection after a long break opens a report of the time away,
/// which "report.away" hands out once.
/// </summary>
public sealed class PresenceObserver(IServiceScopeFactory scopes, TimeProvider time) : IMessageObserver
{
    private const string Seen = "reports.seen";

    public async Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken)
    {
        if (client.AccountId is not { } owner)
        {
            return;
        }

        var now = time.GetUtcNow();
        var first = client.Items.GetValueOrDefault(Seen) is not (Guid seenOwner, DateTimeOffset) || seenOwner != owner;
        if (!first && client.Items[Seen] is (Guid, DateTimeOffset at) && now - at < AwayRules.SeenEvery && now >= at)
        {
            return;
        }

        client.Items[Seen] = (owner, now);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await using var command = await db.CommandAsync(
            """
            INSERT INTO reports.presence (owner_id, last_seen_at) VALUES (@owner, @now)
            ON CONFLICT (owner_id) DO UPDATE SET
                away_from = CASE WHEN @first AND @now - presence.last_seen_at >= @away THEN presence.last_seen_at ELSE presence.away_from END,
                away_to = CASE WHEN @first AND @now - presence.last_seen_at >= @away THEN @now ELSE presence.away_to END,
                last_seen_at = GREATEST(presence.last_seen_at, @now)
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("away", AwayRules.Away);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>What happened while the player was away: money in and out by source and what their factory made.</summary>
public sealed class GetAwayReportHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "report.away";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var owner = client.RequireAccount();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        DateTimeOffset? from = null, to = null;
        // Taking the window clears it, so the report shows once.
        await using (var command = await db.CommandAsync(
            """
            UPDATE reports.presence p SET away_from = NULL, away_to = NULL
            FROM (SELECT owner_id, away_from, away_to FROM reports.presence WHERE owner_id = @owner FOR UPDATE) old
            WHERE p.owner_id = old.owner_id AND old.away_from IS NOT NULL
            RETURNING old.away_from, old.away_to
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                from = reader.GetFieldValue<DateTimeOffset>(0);
                to = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        if (from is not { } start || to is not { } end)
        {
            return new Reply("report.away", new { away = false });
        }

        var money = await scope.ServiceProvider.GetRequiredService<IMoneyHistory>().ByReasonAsync(owner, start, end, 500, cancellationToken);
        var groups = money
            .GroupBy(m => AwayRules.GroupOf(m.Reason))
            .Select(g => (Group: g.Key, Cents: g.Sum(m => m.Cents)))
            .Where(g => g.Cents != 0)
            .OrderByDescending(g => g.Cents)
            .ToList();
        var made = await scope.ServiceProvider.GetRequiredService<IProductionHistory>().MadeAsync(owner, start, end, cancellationToken);
        return new Reply("report.away", new
        {
            away = true,
            from = start.ToUnixTimeMilliseconds(),
            to = end.ToUnixTimeMilliseconds(),
            minutes = (long)(end - start).TotalMinutes,
            income = groups.Where(g => g.Cents > 0).Select(g => new { name = g.Group, cents = g.Cents }),
            expenses = groups.Where(g => g.Cents < 0).Select(g => new { name = g.Group, cents = g.Cents }),
            netCents = groups.Sum(g => g.Cents),
            made = made
                .OrderByDescending(m => m.Value)
                .Take(AwayRules.MadeShown)
                .Select(m => new { id = m.Key, name = Items.Names.GetValueOrDefault(m.Key, m.Key), quantity = m.Value }),
        });
    }
}

/// <summary>The report of what happened while a player was away. Owns the <c>reports</c> schema.</summary>
public sealed class ReportsModule : IGameModule
{
    public string Name => "Reports";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ReportsDbContext>(ReportsDbContext.Schema);
        services.AddSingleton<IMessageObserver, PresenceObserver>();
        services.AddSingleton<IMessageHandler, GetAwayReportHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<ReportsDbContext>(cancellationToken);
}
