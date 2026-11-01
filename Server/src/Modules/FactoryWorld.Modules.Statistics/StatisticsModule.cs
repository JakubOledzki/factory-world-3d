using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.Statistics;

/// <summary>What one player's factory made and used up of one item within one minute.</summary>
public sealed class ProductionRow
{
    public Guid OwnerId { get; set; }

    /// <summary>The start of the minute.</summary>
    public DateTimeOffset Minute { get; set; }

    public string Item { get; set; } = "";

    public long Produced { get; set; }

    public long Consumed { get; set; }
}

public sealed class StatisticsDbContext(DbContextOptions<StatisticsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "stats";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<ProductionRow>(e =>
        {
            e.ToTable("production");
            e.HasKey(p => new { p.OwnerId, p.Minute, p.Item });
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.Minute).HasColumnName("minute");
            e.Property(p => p.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(p => p.Produced).HasColumnName("produced");
            e.Property(p => p.Consumed).HasColumnName("consumed");
        });
    }
}

public static class ProductionRules
{
    /// <summary>How far back the statistics reach; older minutes are dropped.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromHours(24);

    /// <summary>The periods a player can look at, in minutes, and how many minutes one bar of the chart covers.</summary>
    public static readonly IReadOnlyDictionary<int, int> Windows = new Dictionary<int, int>
    {
        [10] = 1,
        [60] = 3,
        [1440] = 60,
    };

    /// <summary>An item counts as short when the factory uses up this much more of it than it makes.</summary>
    public const double ShortageRatio = 1.05;

    public static DateTimeOffset MinuteOf(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
}

/// <summary>Adds what the factory did to the minute it happened in.</summary>
public sealed class ProductionStats(GameDb db, TimeProvider time, GameCounters counters) : IProductionStats
{
    /// <summary>How often a player's minutes older than <see cref="ProductionRules.Kept"/> are dropped while recording, so
    /// the table holds a day of each factory also for those who never look at it (the bots, say).</summary>
    public static readonly TimeSpan PrunedEvery = TimeSpan.FromHours(1);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> Pruned = new();

    public async Task RecordAsync(Guid owner, IReadOnlyCollection<ProductionEntry> entries, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var oldest = now - ProductionRules.Kept;
        if (!Pruned.TryGetValue(owner, out var pruned) || now - pruned >= PrunedEvery || pruned > now)
        {
            await ProductionReports.PruneAsync(db, owner, now, cancellationToken);
            Pruned[owner] = now;
        }

        // Sorted, so that two transactions adding to the same rows lock them in the same order.
        var rows = entries
            .Where(e => e.At > oldest && (e.Produced != 0 || e.Consumed != 0))
            .GroupBy(e => (Minute: ProductionRules.MinuteOf(e.At > now ? now : e.At), e.Item))
            .Select(g => (g.Key.Minute, g.Key.Item, Produced: g.Sum(e => e.Produced), Consumed: g.Sum(e => e.Consumed)))
            .OrderBy(r => r.Minute).ThenBy(r => r.Item, StringComparer.Ordinal)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        await using var command = await db.CommandAsync(
            """
            INSERT INTO stats.production (owner_id, minute, item, produced, consumed)
            SELECT @owner, t.minute, t.item, t.produced, t.consumed
            FROM unnest(@minutes, @items, @produced, @consumed) AS t(minute, item, produced, consumed)
            ON CONFLICT (owner_id, minute, item) DO UPDATE SET
                produced = production.produced + excluded.produced,
                consumed = production.consumed + excluded.consumed
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.Add(new Npgsql.NpgsqlParameter("minutes", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        {
            Value = rows.Select(r => r.Minute).ToArray(),
        });
        command.Parameters.AddWithValue("items", rows.Select(r => r.Item).ToArray());
        command.Parameters.AddWithValue("produced", rows.Select(r => r.Produced).ToArray());
        command.Parameters.AddWithValue("consumed", rows.Select(r => r.Consumed).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
        db.AfterCommit(() =>
        {
            foreach (var row in rows)
            {
                counters.Production(owner, row.Item, row.Produced, row.Consumed);
            }
        });
    }
}

/// <param name="Produced">Per bar of the chart, oldest first.</param>
public sealed record ItemFlow(string Item, long[] Produced, long[] Consumed)
{
    public long TotalProduced => Produced.Sum();

    public long TotalConsumed => Consumed.Sum();
}

/// <summary>Machines of one kind making one product that stand still for the same reason.</summary>
public sealed record Stall(string Kind, string? Product, MachineActivity Activity, IReadOnlyList<string> Missing, int Count);

/// <summary>An item the factory uses up faster than it makes.</summary>
public sealed record Shortage(string Item, double ProducedPerMinute, double ConsumedPerMinute);

public sealed record ProductionReport(
    int Minutes, int BarMinutes, DateTimeOffset Start, DateTimeOffset Now, IReadOnlyList<ItemFlow> Items,
    IReadOnlyList<MachineStatus> Machines, IReadOnlyList<Stall> Stalls, IReadOnlyList<Shortage> Shortages)
{
    /// <summary>Minutes the report covers; the current minute counts as far as it has gone.</summary>
    public double Elapsed => Math.Max(1.0 / 60, (Now - Start).TotalMinutes);
}

public sealed class ProductionHistory(GameDb db) : IProductionHistory
{
    public async Task<IReadOnlyDictionary<string, long>> MadeAsync(Guid owner, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT item, sum(produced)::bigint FROM stats.production
            WHERE owner_id = @owner AND minute >= @from AND minute < @to AND produced > 0
            GROUP BY item
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("from", ProductionRules.MinuteOf(from));
        command.Parameters.AddWithValue("to", to);
        var made = new Dictionary<string, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            made[reader.GetString(0)] = reader.GetInt64(1);
        }

        return made;
    }
}

public sealed class ProductionReports(GameDb db, IEnumerable<IMachineStatuses> machines, IPlotObjects plots, TimeProvider time)
{
    /// <summary>Drops the player's minutes older than the statistics reach.</summary>
    public static async Task PruneAsync(GameDb db, Guid owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var prune = await db.CommandAsync(
            "DELETE FROM stats.production WHERE owner_id = @owner AND minute < @oldest", cancellationToken);
        prune.Parameters.AddWithValue("owner", owner);
        prune.Parameters.AddWithValue("oldest", now - ProductionRules.Kept - TimeSpan.FromMinutes(1));
        await prune.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<ProductionReport> ReportAsync(Guid owner, int minutes, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!ProductionRules.Windows.TryGetValue(minutes, out var bar))
            {
                throw new GameException("bad_period", $"Choose one of: {string.Join(", ", ProductionRules.Windows.Keys)} minutes.");
            }

            var now = time.GetUtcNow();
            await PruneAsync(db, owner, now, cancellationToken);

            // Whole bars, the last one ending with the current minute.
            var end = ProductionRules.MinuteOf(now) + TimeSpan.FromMinutes(1);
            var bars = minutes / bar;
            var start = end - TimeSpan.FromMinutes(minutes);
            var flows = new Dictionary<string, ItemFlow>();
            await using (var command = await db.CommandAsync(
                """
                SELECT minute, item, produced, consumed FROM stats.production
                WHERE owner_id = @owner AND minute >= @start
                """, cancellationToken))
            {
                command.Parameters.AddWithValue("owner", owner);
                command.Parameters.AddWithValue("start", start);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var minute = reader.GetFieldValue<DateTimeOffset>(0);
                    var item = reader.GetString(1);
                    var i = Math.Clamp((int)((minute - start).TotalMinutes / bar), 0, bars - 1);
                    if (!flows.TryGetValue(item, out var flow))
                    {
                        flows[item] = flow = new ItemFlow(item, new long[bars], new long[bars]);
                    }

                    flow.Produced[i] += reader.GetInt64(2);
                    flow.Consumed[i] += reader.GetInt64(3);
                }
            }

            var statuses = new List<MachineStatus>();
            foreach (var module in machines)
            {
                statuses.AddRange(await module.StatusesAsync(owner, cancellationToken));
            }

            var report = new ProductionReport(minutes, bar, start, now,
                flows.Values.OrderByDescending(f => f.TotalProduced + f.TotalConsumed).ThenBy(f => f.Item, StringComparer.Ordinal).ToList(),
                statuses, [], []);
            return report with { Stalls = Stalls(statuses), Shortages = Shortages(report) };
        }, cancellationToken);

    /// <summary>What every machine on one plot is doing now, for the marks over stalled machines.</summary>
    public Task<IReadOnlyList<MachineStatus>> OnPlotAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            var ids = (await plots.ObjectsAsync(x, y, cancellationToken)).Select(o => o.Id).ToHashSet();
            var statuses = new List<MachineStatus>();
            foreach (var module in machines)
            {
                statuses.AddRange((await module.StatusesOnPlotAsync(x, y, ids, cancellationToken)).Where(s => ids.Contains(s.Id)));
            }

            return (IReadOnlyList<MachineStatus>)statuses;
        }, cancellationToken);

    public static IReadOnlyList<Stall> Stalls(IEnumerable<MachineStatus> statuses) =>
        statuses
            .Where(s => s.Activity != MachineActivity.Working)
            .GroupBy(s => (s.Kind, s.Product, s.Activity, Missing: string.Join(",", s.Missing)))
            .Select(g => new Stall(g.Key.Kind, g.Key.Product, g.Key.Activity, g.First().Missing, g.Count()))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<Shortage> Shortages(ProductionReport report) =>
        report.Items
            .Where(f => f.TotalConsumed > 0 && f.TotalConsumed > f.TotalProduced * ProductionRules.ShortageRatio)
            .Select(f => new Shortage(f.Item, f.TotalProduced / report.Elapsed, f.TotalConsumed / report.Elapsed))
            .OrderByDescending(s => s.ConsumedPerMinute - s.ProducedPerMinute)
            .ToList();
}

public sealed record ProductionRequest(int Minutes);

/// <summary>What the player's factory made and used up per minute lately, and which machines stand still and why.</summary>
public sealed class GetProductionHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "stats.production";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var minutes = payload is { ValueKind: JsonValueKind.Object } ? Payload.Read<ProductionRequest>(payload).Minutes : 10;
        await using var scope = scopes.CreateAsyncScope();
        var r = await scope.ServiceProvider.GetRequiredService<ProductionReports>().ReportAsync(client.RequireAccount(), minutes, cancellationToken);
        return new Reply("stats.production", new
        {
            minutes = r.Minutes,
            periods = ProductionRules.Windows.Keys,
            barMinutes = r.BarMinutes,
            // Unix milliseconds of where the first bar starts.
            start = r.Start.ToUnixTimeMilliseconds(),
            serverTime = r.Now.ToUnixTimeMilliseconds(),
            items = r.Items.Select(f => new
            {
                id = f.Item,
                name = Name(f.Item),
                produced = f.TotalProduced,
                consumed = f.TotalConsumed,
                producedPerMinute = Math.Round(f.TotalProduced / r.Elapsed, 2),
                consumedPerMinute = Math.Round(f.TotalConsumed / r.Elapsed, 2),
                producedBars = f.Produced,
                consumedBars = f.Consumed,
            }),
            machines = r.Machines.GroupBy(m => m.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new
            {
                kind = g.Key,
                name = Name(g.Key),
                total = g.Count(),
                working = g.Count(m => m.Activity == MachineActivity.Working),
            }),
            stalls = r.Stalls.Select(s => new
            {
                kind = s.Kind,
                name = Name(s.Kind),
                product = s.Product,
                productName = s.Product is null ? null : Name(s.Product),
                reason = Reason(s.Activity),
                missing = s.Missing.Select(m => new { id = m, name = Name(m) }),
                count = s.Count,
            }),
            shortages = r.Shortages.Select(s => new
            {
                id = s.Item,
                name = Name(s.Item),
                producedPerMinute = Math.Round(s.ProducedPerMinute, 2),
                consumedPerMinute = Math.Round(s.ConsumedPerMinute, 2),
            }),
        });
    }

    private static string Name(string item) => Items.Names.GetValueOrDefault(item, item);

    public static string Reason(MachineActivity activity) => activity switch
    {
        MachineActivity.NoInput => "no_input",
        MachineActivity.NoFuel => "no_fuel",
        MachineActivity.OutputFull => "output_full",
        MachineActivity.NoRecipe => "no_recipe",
        MachineActivity.NoPower => "no_power",
        _ => "working",
    };
}

public sealed record PlotMachinesRequest(int X, int Y);

/// <summary>What each machine on a plot is doing, so the plot marks the ones that stand still and why.</summary>
public sealed class GetPlotMachinesHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "stats.machines";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlotMachinesRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var statuses = await scope.ServiceProvider.GetRequiredService<ProductionReports>()
            .OnPlotAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        return new Reply("stats.machines", new
        {
            x = request.X,
            y = request.Y,
            machines = statuses.Select(s => new
            {
                id = s.Id,
                kind = s.Kind,
                state = GetProductionHandler.Reason(s.Activity),
                fired = s.Fired,
                missing = s.Missing.Select(m => new { id = m, name = Items.Names.GetValueOrDefault(m, m) }),
            }),
        });
    }
}

/// <summary>Production statistics: what each player's factory makes and uses up, and where it stalls. Owns the <c>stats</c> schema.</summary>
public sealed class StatisticsModule : IGameModule
{
    public string Name => "Statistics";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<StatisticsDbContext>(StatisticsDbContext.Schema);
        services.AddScoped<IProductionStats, ProductionStats>();
        services.AddScoped<ProductionReports>();
        services.AddScoped<IProductionHistory, ProductionHistory>();
        services.AddSingleton<EfficiencyScores>();
        services.AddScoped<IPlayerRanking, EfficiencyRanking>();
        services.AddSingleton<IMessageHandler, GetProductionHandler>();
        services.AddSingleton<IMessageHandler, GetPlotMachinesHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<StatisticsDbContext>(cancellationToken);
}
