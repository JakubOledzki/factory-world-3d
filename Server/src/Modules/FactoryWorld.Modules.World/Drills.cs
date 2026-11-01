using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>How a drill of one level works.</summary>
/// <param name="Time">How long it takes to dig one load, before the biome's bonus.</param>
/// <param name="Capacity">Most ore it keeps; full, it stops until ore is taken out.</param>
public sealed record DrillLevel(string Kind, int Level, TimeSpan Time, int Capacity);

/// <summary>
/// A drill stands on a tile next to a mine of a mountain plot, one at each mine, and digs it by itself
/// on power from the plot's grid: one load after another, the same loads as with a pickaxe, from the
/// same mine. When the mine runs dry it waits until the mine has filled up again. The ore waits in it
/// until taken out by hand or by an inserter standing next to it. The heavy drill digs faster and holds more.
/// </summary>
public static class Drills
{
    public static readonly IReadOnlyList<DrillLevel> Levels =
    [
        new(Items.Drill, 1, TimeSpan.FromSeconds(6), 100),
        new(Items.HeavyDrill, 2, TimeSpan.FromSeconds(2.5), 200),
    ];

    public static DrillLevel? Level(string kind) => Levels.FirstOrDefault(l => l.Kind == kind);

    /// <summary>Ore taken out by hand gives one experience point for every this many.</summary>
    public const int OresPerXp = 4;

    /// <summary>The mine a drill standing on (tileX, tileY) works: the one whose tiles it touches, or null.</summary>
    public static MineSite? MineAt(IEnumerable<MineSite> mines, int tileX, int tileY) =>
        mines.FirstOrDefault(m => tileX >= m.X - 1 && tileX <= m.X + MineSite.Width && tileY >= m.Y - 1 && tileY <= m.Y + MineSite.Height);
}

/// <summary>
/// One drill and its mine as they work through time, in memory. The drill counts its loads in its own
/// time on the power grid (<see cref="IPowerClock"/>); the mine fills up again in plain time, which is
/// taken to have gone by as fast as the drill's own since.
/// </summary>
/// <param name="startedAt">When, in the drill's own time, the load it digs now started; null while it stands idle.</param>
/// <param name="waiting">Whether it waits for its dry mine to fill up again.</param>
public sealed class DrillPatch(
    DrillLevel level, double speed, MineLevel mine, Mineral mineral, int used, DateTimeOffset? emptyAt,
    Dictionary<string, int> ores, DateTimeOffset? startedAt, bool waiting)
{
    public DrillLevel DrillLevel { get; } = level;

    public MineLevel MineLevel { get; } = mine;

    /// <summary>Loads taken from the mine since it was last full, and when it ran dry.</summary>
    public int Used { get; private set; } = used;

    public DateTimeOffset? EmptyAt { get; private set; } = emptyAt;

    public Dictionary<string, int> Ores { get; } = ores;

    public DateTimeOffset? StartedAt { get; private set; } = startedAt;

    public bool Waiting { get; private set; } = waiting;

    /// <summary>Ore it dug, for the production statistics.</summary>
    public List<ProductionEntry> Made { get; } = [];

    public bool Changed { get; private set; }

    public bool MineChanged { get; private set; }

    public int Stored => Ores.Values.Sum();

    /// <summary>Full when one more load might not fit.</summary>
    public bool Full => Stored + Mining.MaxOres > DrillLevel.Capacity;

    /// <summary>How long one load takes on this plot.</summary>
    public TimeSpan LoadTime => DrillLevel.Time / speed;

    /// <summary>When its dry mine is full again, or null.</summary>
    public DateTimeOffset? RefillAt => EmptyAt + MineLevel.Refill;

    /// <summary>Digs every load it could have dug by <paramref name="now"/>, when its own time is <paramref name="machineNow"/>.</summary>
    public void Advance(DateTimeOffset now, DateTimeOffset machineNow)
    {
        // Plain time of a moment in the drill's own time, as if it had full power since.
        DateTimeOffset Real(DateTimeOffset machine) => machine >= machineNow ? now : now - (machineNow - machine);

        for (var guard = 0; guard < DrillLevel.Capacity + 4; guard++)
        {
            if (StartedAt is not { } started)
            {
                if (!Full)
                {
                    StartedAt = machineNow;
                    Waiting = EmptyAt is not null && RefillAt > now;
                    Changed = true;
                }

                return;
            }

            if (Waiting)
            {
                if (RefillAt is { } refill && refill > now)
                {
                    return;
                }

                // Full again (or built up meanwhile): from the moment it filled up, not before the drill stopped.
                var from = EmptyAt is not null ? machineNow - (now - RefillAt!.Value) : machineNow;
                StartedAt = from > started ? from : started;
                if (EmptyAt is not null)
                {
                    (Used, EmptyAt, MineChanged) = (0, null, true);
                }

                Waiting = false;
                Changed = true;
                continue;
            }

            if (Full)
            {
                StartedAt = null;
                Changed = true;
                return;
            }

            var end = started + LoadTime;
            if (end > machineNow)
            {
                return;
            }

            var done = Real(end);
            if (EmptyAt is not null)
            {
                if (RefillAt <= done)
                {
                    (Used, EmptyAt, MineChanged) = (0, null, true);
                }
                else
                {
                    Waiting = true;
                    Changed = true;
                    continue;
                }
            }

            foreach (var (item, quantity) in Mining.Load(MineLevel, mineral))
            {
                Ores[item] = Ores.GetValueOrDefault(item) + quantity;
                Made.Add(ProductionEntry.Made(item, quantity, done));
            }

            Used++;
            if (Used >= MineLevel.Units)
            {
                EmptyAt = done;
            }

            StartedAt = end;
            Changed = true;
            MineChanged = true;
        }
    }

    /// <summary>Takes ore out at <paramref name="machineNow"/>; a drill that stood full starts again from then.</summary>
    public int Take(string item, int quantity, DateTimeOffset machineNow)
    {
        var taken = Math.Min(quantity, Ores.GetValueOrDefault(item));
        if (taken == 0)
        {
            return 0;
        }

        Ores[item] -= taken;
        if (Ores[item] == 0)
        {
            Ores.Remove(item);
        }

        if (StartedAt is null && !Full)
        {
            StartedAt = machineNow;
        }

        Changed = true;
        return taken;
    }

    /// <summary>When, in plain time, it next has a load dug or its mine is full again; null while it stands full.</summary>
    public DateTimeOffset? NextChange(DateTimeOffset now, IPowerClock clock)
    {
        if (Waiting)
        {
            return RefillAt;
        }

        return StartedAt is { } started && !Full ? clock.When(started + LoadTime, now) : null;
    }
}

/// <summary>A drill on its plot, the mine it works and how it is doing.</summary>
public sealed record DrillOnPlot(PlotObject Object, MineSite Mine, DrillPatch Patch, IPowerClock Clock);

public sealed class DrillService(
    GameDb db, WorldMap map, PlotService plots, IPlotObjects objects, IInventory inventory, ICharacterProgress progress, IProductionStats stats,
    IPlotPower power, TimeProvider time, ITechnologies technologies) : IPlacedHook, IPlotAutomation, IMachineContents, IMachinePorts, IMachineStatuses
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A drill on a plot the player may work on, worked up to now.</summary>
    public Task<DrillOnPlot> StateAsync(Guid player, int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            return await WorkedAsync(x, y, id, automation, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes the ore out of a drill into the player's inventory, each kind as far as it fits; the rest stays in.</summary>
    public Task<(DrillOnPlot Drill, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp)> TakeAsync(
        Guid player, int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            var drill = await WorkedAsync(x, y, id, automation, cancellationToken);
            var now = time.GetUtcNow();
            var machineNow = drill.Clock.At(now);
            var taken = new List<(string, int)>();
            foreach (var (item, quantity) in drill.Patch.Ores.ToList())
            {
                var fits = (int)await inventory.RoomForAsync(player, item, quantity, cancellationToken);
                if (fits > 0)
                {
                    drill.Patch.Take(item, fits, machineNow);
                    await inventory.AddAsync(player, item, fits, cancellationToken);
                    taken.Add((item, fits));
                }
            }

            if (taken.Count == 0 && drill.Patch.Stored > 0)
            {
                throw InventoryExtensions.InventoryFull();
            }

            drill.Patch.Advance(now, machineNow);
            await SaveAsync(x, y, drill, cancellationToken);
            var total = taken.Sum(t => t.Item2);
            XpGain? xp = total > 0 ? await progress.AddXpAsync(player, total / Drills.OresPerXp, cancellationToken) : null;
            return (drill, (IReadOnlyList<(string, int)>)taken, xp);
        }, cancellationToken);

    public async Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken)
    {
        if (!Items.IsDrill(placed.Kind) || Drills.MineAt(PlotLayout.Mines(x, y, map.BiomeAt(x, y)), placed.TileX, placed.TileY) is not { } mine)
        {
            return;
        }

        await using (var insert = await db.CommandAsync(
            """
            INSERT INTO world.plot_drills (object_id, x, y, owner_id, mine, ores, started_at, waiting)
            VALUES (@id, @x, @y, @owner, @mine, '{}', NULL, false) ON CONFLICT DO NOTHING
            """, cancellationToken))
        {
            insert.Parameters.AddWithValue("id", placed.Id);
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("owner", owner);
            insert.Parameters.AddWithValue("mine", mine.Id);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        // It starts digging at once.
        await CatchUpAsync(x, y, cancellationToken);
    }

    /// <summary>Works every drill of the plot up to now; called before anything is put on the plot or taken off, and before a mine is worked by hand.</summary>
    public Task CatchUpAsync(int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var now = time.GetUtcNow();
            foreach (var drill in await OpenAsync(x, y, cancellationToken))
            {
                drill.Patch.Advance(now, drill.Clock.At(now));
                await SaveAsync(x, y, drill, cancellationToken);
            }
        }, cancellationToken);

    /// <summary>The drill is being picked up (worked up to now just before): its ore goes to the player.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync("DELETE FROM world.plot_drills WHERE object_id = @id RETURNING ores::text", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        if (await delete.ExecuteScalarAsync(cancellationToken) is string ores)
        {
            foreach (var (item, quantity) in JsonSerializer.Deserialize<Dictionary<string, int>>(ores, Json) ?? [])
            {
                if (quantity > 0 && Items.Exists(item))
                {
                    await inventory.AddAsync(owner, item, quantity, cancellationToken);
                }
            }
        }
    }

    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (!Items.IsDrill(kind) || await objects.PlotOfAsync(id, cancellationToken) is not var (x, y))
        {
            return null;
        }

        return (await OpenAsync(x, y, cancellationToken)).FirstOrDefault(d => d.Object.Id == id) is { } drill ? new Port(this, x, y, drill) : null;
    }

    public async Task<IReadOnlyList<MachineStatus>> StatusesAsync(Guid owner, CancellationToken cancellationToken)
    {
        var plotsOf = new List<(int X, int Y)>();
        await using (var command = await db.CommandAsync("SELECT DISTINCT x, y FROM world.plot_drills WHERE owner_id = @owner", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                plotsOf.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
        }

        var statuses = new List<MachineStatus>();
        foreach (var (x, y) in plotsOf)
        {
            statuses.AddRange(await OnPlotAsync(x, y, owner, cancellationToken));
        }

        return statuses;
    }

    public Task<IReadOnlyList<MachineStatus>> StatusesOnPlotAsync(int x, int y, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        OnPlotAsync(x, y, null, cancellationToken);

    /// <summary>The plot's drills, only <paramref name="owner"/>'s when given.</summary>
    private async Task<IReadOnlyList<MachineStatus>> OnPlotAsync(int x, int y, Guid? owner, CancellationToken cancellationToken)
    {
        var statuses = new List<MachineStatus>();
        var now = time.GetUtcNow();
        foreach (var drill in (await OpenAsync(x, y, cancellationToken)).Where(d => owner is null || d.Object.Owner == owner))
        {
            drill.Patch.Advance(now, drill.Clock.At(now));
            await SaveAsync(x, y, drill, cancellationToken);
            var activity = drill.Patch.Full ? MachineActivity.OutputFull
                : drill.Clock.Speed(now) <= 0 ? MachineActivity.NoPower
                : drill.Patch.Waiting ? MachineActivity.NoInput
                : MachineActivity.Working;
            statuses.Add(new MachineStatus(drill.Object.Id, drill.Object.Kind, null, activity, []));
        }

        return statuses;
    }

    /// <summary>A drill of the plot after the plot's machinery (inserters may have taken ore out) is caught up.</summary>
    private async Task<DrillOnPlot> WorkedAsync(int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken)
    {
        foreach (var machinery in automation)
        {
            if (!ReferenceEquals(machinery, this))
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }
        }

        var now = time.GetUtcNow();
        var drill = (await OpenAsync(x, y, cancellationToken)).FirstOrDefault(d => d.Object.Id == id)
            ?? throw new GameException("no_object", "There is no such drill on this plot.");
        drill.Patch.Advance(now, drill.Clock.At(now));
        await SaveAsync(x, y, drill, cancellationToken);
        return drill;
    }

    /// <summary>The drills of a plot with their mines, both locked until the transaction ends.</summary>
    private async Task<List<DrillOnPlot>> OpenAsync(int x, int y, CancellationToken cancellationToken)
    {
        var placed = (await objects.ObjectsAsync(x, y, cancellationToken)).Where(o => Items.IsDrill(o.Kind)).ToDictionary(o => o.Id);
        if (placed.Count == 0)
        {
            return [];
        }

        var rows = new List<(Guid Id, int Mine, Dictionary<string, int> Ores, DateTimeOffset? StartedAt, bool Waiting)>();
        await using (var command = await db.CommandAsync(
            "SELECT object_id, mine, ores::text, started_at, waiting FROM world.plot_drills WHERE x = @x AND y = @y ORDER BY object_id FOR UPDATE",
            cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetInt32(1), JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(2), Json) ?? [],
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetBoolean(4)));
            }
        }

        var biome = map.BiomeAt(x, y);
        var mines = PlotLayout.Mines(x, y, biome);
        var mineral = Deposits.MineralIn(x, y, biome);
        var result = new List<DrillOnPlot>();
        foreach (var row in rows)
        {
            if (!placed.TryGetValue(row.Id, out var o) || mines.FirstOrDefault(m => m.Id == row.Mine) is not { } site)
            {
                continue;
            }

            var (used, emptyAt, level) = await MineRows.LockAsync(db, x, y, row.Mine, cancellationToken);
            // The drill's owner's geology makes the mineral come up more often.
            var rich = (await technologies.ResearchedAsync(o.Owner, cancellationToken)).Contains(GeologyTech.Id);
            var patch = new DrillPatch(
                Drills.Level(o.Kind)!, Mining.Speed(biome), Mining.Level(level), mineral with { Rich = rich }, used, emptyAt, row.Ores, row.StartedAt, row.Waiting);
            result.Add(new DrillOnPlot(o, site, patch, await power.ClockAsync(o.Id, cancellationToken)));
        }

        return result;
    }

    /// <summary>Writes back a drill that worked, and its mine.</summary>
    private async Task SaveAsync(int x, int y, DrillOnPlot drill, CancellationToken cancellationToken)
    {
        var patch = drill.Patch;
        if (patch.Changed)
        {
            await using var update = await db.CommandAsync(
                "UPDATE world.plot_drills SET ores = @ores::jsonb, started_at = @started, waiting = @waiting WHERE object_id = @id", cancellationToken);
            update.Parameters.AddWithValue("id", drill.Object.Id);
            update.Parameters.AddWithValue("ores", JsonSerializer.Serialize(patch.Ores, Json));
            update.Parameters.Add(new Npgsql.NpgsqlParameter("started", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)patch.StartedAt ?? DBNull.Value });
            update.Parameters.AddWithValue("waiting", patch.Waiting);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (patch.MineChanged)
        {
            await MineRows.WriteAsync(db, x, y, drill.Mine.Id, patch.Used, patch.EmptyAt, patch.MineLevel.Level, cancellationToken);
        }

        if (patch.Made.Count > 0)
        {
            await stats.RecordAsync(drill.Object.Owner, patch.Made.ToList(), cancellationToken);
            patch.Made.Clear();
        }
    }

    /// <summary>A drill as inserters see it: ore comes out, nothing goes in.</summary>
    private sealed class Port(DrillService service, int x, int y, DrillOnPlot drill) : IMachinePort
    {
        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            Advance(now);
            return drill.Patch.Ores.Where(o => o.Value > 0).Select(o => o.Key).FirstOrDefault(wanted);
        }

        public void Take(string item, DateTimeOffset now)
        {
            Advance(now);
            drill.Patch.Take(item, 1, drill.Clock.At(now));
        }

        public bool Accepts(string item, DateTimeOffset now) => false;

        public void Put(string item, DateTimeOffset now)
        {
        }

        public DateTimeOffset? NextChange(DateTimeOffset now)
        {
            Advance(now);
            return drill.Patch.NextChange(now, drill.Clock);
        }

        public Task SaveAsync(CancellationToken cancellationToken) => service.SaveAsync(x, y, drill, cancellationToken);

        private void Advance(DateTimeOffset now) => drill.Patch.Advance(now, drill.Clock.At(now));
    }
}

public sealed record DrillRequest(int X, int Y, Guid Id);

public abstract class DrillHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<DrillRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var (drill, taken, xp) = await RunAsync(
            scope.ServiceProvider.GetRequiredService<DrillService>(), scope.ServiceProvider.GetServices<IPlotAutomation>(),
            client.RequireAccount(), request, cancellationToken);
        var now = time.GetUtcNow();
        var patch = drill.Patch;
        var next = Drills.Levels.FirstOrDefault(l => l.Level == patch.DrillLevel.Level + 1);
        return new Reply("drill", new
        {
            id = drill.Object.Id,
            kind = drill.Object.Kind,
            name = Items.Names[drill.Object.Kind],
            level = patch.DrillLevel.Level,
            mine = drill.Mine.Id,
            mineLevel = patch.MineLevel.Level,
            // Seconds a load takes here with all the power it needs, and how fast it works now (0 without power).
            seconds = patch.LoadTime.TotalSeconds,
            speed = drill.Clock.Speed(now),
            capacity = patch.DrillLevel.Capacity,
            stored = patch.Stored,
            ores = patch.Ores.Where(o => o.Value > 0).OrderByDescending(o => o.Value)
                .Select(o => new { id = o.Key, name = Items.Names[o.Key], quantity = o.Value }),
            full = patch.Full,
            // Waiting for its dry mine, and when that is full again (Unix milliseconds).
            waiting = patch.Waiting,
            refillAt = patch.Waiting ? patch.RefillAt?.ToUnixTimeMilliseconds() : null,
            unitsLeft = patch.EmptyAt is null ? Math.Max(0, patch.MineLevel.Units - patch.Used) : 0,
            units = patch.MineLevel.Units,
            upgrade = next is null ? null : new { kind = next.Kind, name = Items.Names[next.Kind], seconds = (next.Time / Mining.Speed(
                scope.ServiceProvider.GetRequiredService<WorldMap>().BiomeAt(request.X, request.Y))).TotalSeconds, capacity = next.Capacity },
            taken = taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = xp is { } gain ? new { xp = gain.Xp, level = gain.Level, leveledUp = gain.LeveledUp } : null,
            serverTime = now.ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<(DrillOnPlot, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        DrillService drills, IEnumerable<IPlotAutomation> automation, Guid player, DrillRequest request, CancellationToken cancellationToken);
}

/// <summary>A drill and the ore in it, worked up to now.</summary>
public sealed class GetDrillHandler(IServiceScopeFactory scopes, TimeProvider time) : DrillHandler(scopes, time)
{
    public override string Type => "drill.get";

    protected override async Task<(DrillOnPlot, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        DrillService drills, IEnumerable<IPlotAutomation> automation, Guid player, DrillRequest request, CancellationToken cancellationToken) =>
        (await drills.StateAsync(player, request.X, request.Y, request.Id, automation, cancellationToken), [], null);
}

/// <summary>Takes all the ore out of a drill into the inventory.</summary>
public sealed class TakeFromDrillHandler(IServiceScopeFactory scopes, TimeProvider time) : DrillHandler(scopes, time)
{
    public override string Type => "drill.take";

    protected override Task<(DrillOnPlot, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        DrillService drills, IEnumerable<IPlotAutomation> automation, Guid player, DrillRequest request, CancellationToken cancellationToken) =>
        drills.TakeAsync(player, request.X, request.Y, request.Id, automation, cancellationToken);
}
