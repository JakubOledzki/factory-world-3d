using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>How a farmer's building of one level works.</summary>
/// <param name="Radius">Its fields reach this many tiles from its own tile each way: a square of 2 × Radius + 1.</param>
/// <param name="Capacity">Most crops it keeps; full, it stops reaping until crops are taken out.</param>
/// <param name="GrowSpeed">How many times faster than sown by hand a crop grows in its care.</param>
/// <param name="ReapTime">How long it takes to reap one field; it reaps one after another.</param>
public sealed record FarmLevel(string Kind, int Level, int Radius, int Capacity, double GrowSpeed, TimeSpan ReapTime);

/// <summary>
/// A farmer's farmstead stands on 3 × 3 tiles of a fertile plot and farms the square around it, as a forester's building keeps a forest:
/// it tills every free tile there into a field, sows the crop it is told (wheat at first), reaps each field once it is ripe and sows it again
/// at once. It keeps a clearing one tile wide around itself and a path three tiles wide from its door to the edge of its fields. The crops wait
/// in it until taken out by hand or by an inserter standing next to it; manure put in (by hand or by an inserter) goes under the seed, a piece
/// a field, and the crop grows a quarter faster. Fields tilled by hand in its square stay the player's. The manor farm is the same, with more
/// fields that grow faster, quicker reaping and room for more.
/// </summary>
public static class Farmsteads
{
    public static readonly IReadOnlyList<FarmLevel> Levels =
    [
        new(Items.Farmstead, 1, 4, 200, 1, TimeSpan.FromSeconds(4)),
        new(Items.ManorFarm, 2, 5, 400, 1.5, TimeSpan.FromSeconds(2)),
        // Tractors and combines: the biggest fields, sown and reaped twice as fast as by a farmstead.
        new(Items.MechanizedFarm, 3, 6, 800, 2, TimeSpan.FromSeconds(1)),
    ];

    public static FarmLevel? Level(string kind) => Levels.FirstOrDefault(l => l.Kind == kind);

    /// <summary>The clearing around the building reaches this far from its tile: the building itself and one tile more.</summary>
    public const int Clearing = 2;

    /// <summary>Manure it keeps at most.</summary>
    public const int MaxManure = 50;

    /// <summary>Crops taken out by hand give one experience point for every this many.</summary>
    public const int CropsPerXp = 8;

    /// <summary>How long <paramref name="crop"/> grows in its care, on manure or without.</summary>
    public static TimeSpan GrowTime(FarmLevel level, Crop crop, bool manure) => crop.Grow / (level.GrowSpeed * (manure ? Farming.ManureSpeed : 1));

    /// <summary>Whether it farms the tile (dx, dy) away from its own: inside its square, outside its clearing and path.</summary>
    public static bool Farms(FarmLevel level, int rotation, int dx, int dy)
    {
        if (!Claims(level, dx, dy) || (Math.Abs(dx) <= Clearing && Math.Abs(dy) <= Clearing))
        {
            return false;
        }

        var (fx, fy) = Foresters.Facing(rotation);
        return !(dx * fx + dy * fy > 0 && Math.Abs(dx * fy - dy * fx) <= Footprints.Reach(level.Kind));
    }

    public static bool Claims(FarmLevel level, int dx, int dy) => Math.Abs(dx) <= level.Radius && Math.Abs(dy) <= level.Radius;

    /// <summary>The tiles of the plot each farmstead farms; where two squares meet, a tile belongs to the one that comes first
    /// from north to south, west to east, and none farms another's clearing or path.</summary>
    public static IReadOnlyDictionary<Guid, List<(int X, int Y)>> Areas(IReadOnlyList<PlotObject> farmsteads)
    {
        var ordered = farmsteads.OrderBy(f => f.TileY).ThenBy(f => f.TileX).ThenBy(f => f.Id).ToList();
        var areas = new Dictionary<Guid, List<(int, int)>>();
        var taken = new HashSet<(int, int)>();
        foreach (var f in ordered)
        {
            var level = Level(f.Kind)!;
            var tiles = new List<(int, int)>();
            for (var dy = -level.Radius; dy <= level.Radius; dy++)
            {
                for (var dx = -level.Radius; dx <= level.Radius; dx++)
                {
                    var (x, y) = (f.TileX + dx, f.TileY + dy);
                    if (!PlotLayout.Inside(x, y) || !Farms(level, f.Rotation, dx, dy) || taken.Contains((x, y)))
                    {
                        continue;
                    }

                    var kept = ordered.Any(o => o.Id != f.Id && Level(o.Kind) is { } other
                        && Claims(other, x - o.TileX, y - o.TileY) && !Farms(other, o.Rotation, x - o.TileX, y - o.TileY));
                    if (!kept)
                    {
                        tiles.Add((x, y));
                        taken.Add((x, y));
                    }
                }
            }

            areas[f.Id] = tiles;
        }

        return areas;
    }
}

/// <summary>One farmstead's fields as they work through time, in memory: worked out up to a moment, then written back.</summary>
/// <param name="crop">The crop it sows.</param>
/// <param name="store">The crops it keeps, by item.</param>
/// <param name="manure">Manure it keeps for sowing.</param>
/// <param name="busyUntil">When it is done with the field it reaps now, or could start the next one.</param>
/// <param name="smog">How fast crops grow in the smog over the plot, 0.5 to 1.</param>
public sealed class FarmPatch(FarmLevel level, Crop crop, IReadOnlyDictionary<string, int> store, int manure, DateTimeOffset busyUntil, double smog = 1)
{
    public FarmLevel FarmLevel { get; } = level;

    public Crop Crop { get; private set; } = crop;

    public Dictionary<string, int> Store { get; } = store.Where(s => s.Value > 0).ToDictionary();

    public int Manure { get; private set; } = manure;

    public int Crops => Store.Values.Sum();

    public DateTimeOffset BusyUntil { get; private set; } = busyUntil;

    /// <summary>Its sown fields: what grows on each and when it is ripe.</summary>
    public Dictionary<(int X, int Y), (Crop Crop, DateTimeOffset ReadyAt)> Fields { get; } = [];

    /// <summary>Its fields with nothing on them (e.g. reaped by hand), to be sown the next time it works.</summary>
    public HashSet<(int X, int Y)> Bare { get; } = [];

    /// <summary>Free tiles of its square, to be tilled and sown the next time it works.</summary>
    public HashSet<(int X, int Y)> Open { get; } = [];

    /// <summary>Fields it sowed, as rows to write.</summary>
    public HashSet<(int X, int Y)> Sown { get; } = [];

    public List<ProductionEntry> Made { get; } = [];

    public bool Changed { get; private set; }

    public bool Full => Crops >= FarmLevel.Capacity;

    /// <summary>From now on it sows <paramref name="crop"/>; what grows already is reaped as it is.</summary>
    public void Sow(Crop crop)
    {
        if (crop != Crop)
        {
            Crop = crop;
            Changed = true;
        }
    }

    /// <summary>Puts manure in, as much as there is room for; returns how much went in.</summary>
    public int AddManure(int quantity)
    {
        var added = Math.Clamp(quantity, 0, Farmsteads.MaxManure - Manure);
        Manure += added;
        Changed |= added > 0;
        return added;
    }

    private void SowAt((int X, int Y) tile, DateTimeOffset at)
    {
        var manure = Manure > 0;
        if (manure)
        {
            Manure--;
            Made.Add(ProductionEntry.Used(Items.Manure, 1, at));
        }

        // Rain makes it grow faster, a drought slower.
        Fields[tile] = (Crop, FieldWeather.Ripe(at, Farmsteads.GrowTime(FarmLevel, Crop, manure) / smog));
        Sown.Add(tile);
    }

    /// <summary>Tills and sows the free tiles and bare fields at <paramref name="now"/>, then reaps every field it could have reaped by then.</summary>
    public void Advance(DateTimeOffset now)
    {
        foreach (var tile in Open.Concat(Bare).ToList())
        {
            SowAt(tile, now);
            Changed = true;
        }

        Open.Clear();
        Bare.Clear();
        if (Full || NextReap() is not { } next || next > now)
        {
            return;
        }

        var queue = new PriorityQueue<(int X, int Y), DateTimeOffset>();
        foreach (var (tile, field) in Fields)
        {
            if (field.ReadyAt + FarmLevel.ReapTime <= now)
            {
                queue.Enqueue(tile, field.ReadyAt);
            }
        }

        while (!Full && queue.TryPeek(out var tile, out var ready))
        {
            var done = (ready > BusyUntil ? ready : BusyUntil) + FarmLevel.ReapTime;
            if (done > now)
            {
                break;
            }

            queue.Dequeue();
            var crop = Fields[tile].Crop;
            // What does not fit in the store is left in the field.
            var reaped = Math.Min(crop.Yield, FarmLevel.Capacity - Crops);
            Store[crop.Item] = Store.GetValueOrDefault(crop.Item) + reaped;
            BusyUntil = done;
            Made.Add(ProductionEntry.Made(crop.Item, reaped, done));
            SowAt(tile, done);
            var ripe = Fields[tile].ReadyAt;
            if (ripe + FarmLevel.ReapTime <= now)
            {
                queue.Enqueue(tile, ripe);
            }
        }

        Changed = true;
    }

    /// <summary>When it will next have a field reaped, if it has room for the crop; null when it has no fields.</summary>
    public DateTimeOffset? NextReap()
    {
        DateTimeOffset? first = null;
        foreach (var (_, ready) in Fields.Values)
        {
            if (first is null || ready < first)
            {
                first = ready;
            }
        }

        return first is { } at ? (at > BusyUntil ? at : BusyUntil) + FarmLevel.ReapTime : null;
    }

    /// <summary>Takes crops out at <paramref name="now"/>, of <paramref name="item"/> or any; one that stood full starts reaping again only from then.</summary>
    public int Take(int quantity, DateTimeOffset now, string? item = null)
    {
        var taken = 0;
        foreach (var kind in Store.Keys.Where(k => item is null || k == item).Order().ToList())
        {
            var some = Math.Min(quantity - taken, Store[kind]);
            if (some == 0)
            {
                continue;
            }

            if (Full && BusyUntil < now)
            {
                BusyUntil = now;
            }

            Store[kind] -= some;
            if (Store[kind] == 0)
            {
                Store.Remove(kind);
            }

            taken += some;
            Changed = true;
        }

        return taken;
    }

    public IReadOnlyList<(string Item, int Quantity)> TakeAll(DateTimeOffset now)
    {
        var all = Store.OrderBy(s => s.Key).Select(s => (s.Key, s.Value)).ToList();
        Take(int.MaxValue, now);
        return all;
    }
}

public sealed record FarmsteadOnPlot(PlotObject Object, FarmPatch Patch);

public sealed class FarmsteadService(
    GameDb db, WorldMap map, PlotService plots, IPlotObjects objects, IInventory inventory, ICharacterProgress progress, IProductionStats stats,
    TimeProvider time, ITechnologies technologies, SmogService smog) : IPlacedHook, IPlotAutomation, IMachineContents, IMachinePorts, IMachineStatuses
{
    /// <summary>The plot's farmsteads worked up to now.</summary>
    public Task<IReadOnlyList<FarmsteadOnPlot>> StateAsync(Guid player, int x, int y, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            foreach (var machinery in automation)
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }

            return await ReportAsync(x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes all the crops out of a farmstead on the player's plot into the inventory.</summary>
    public Task<(IReadOnlyList<FarmsteadOnPlot> State, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp)> TakeAsync(
        Guid player, int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            foreach (var machinery in automation)
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }

            var now = time.GetUtcNow();
            var farms = await OpenAsync(x, y, now, cancellationToken);
            var farm = Find(farms, id);
            var taken = new List<(string Item, int Quantity)>();
            foreach (var (item, quantity) in farm.Patch.Store.OrderBy(s => s.Key).ToList())
            {
                var fits = (int)await inventory.RoomForAsync(player, item, quantity, cancellationToken);
                if (fits > 0)
                {
                    farm.Patch.Take(fits, now, item);
                    await inventory.AddAsync(player, item, fits, cancellationToken);
                    taken.Add((item, fits));
                }
            }

            if (taken.Count == 0 && farm.Patch.Store.Count > 0)
            {
                throw InventoryExtensions.InventoryFull();
            }

            farm.Patch.Advance(now);
            await SaveAsync(x, y, farms, cancellationToken);

            XpGain? xp = taken.Count > 0 ? await progress.AddXpAsync(player, taken.Sum(t => t.Quantity) / Farmsteads.CropsPerXp, cancellationToken) : null;
            return (await ReportAsync(x, y, cancellationToken), (IReadOnlyList<(string, int)>)taken, xp);
        }, cancellationToken);

    /// <summary>Sets the crop a farmstead sows from now on, and puts <paramref name="manure"/> from the inventory into it.</summary>
    public Task<IReadOnlyList<FarmsteadOnPlot>> SowAsync(
        Guid player, int x, int y, Guid id, string? crop, int manure, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            Crop? kind = null;
            if (crop is not null)
            {
                kind = Farming.ById.GetValueOrDefault(crop) ?? throw new GameException("unknown_crop", "There is no such crop.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            if (kind is not null)
            {
                await technologies.RequireUnlockedAsync(player, kind.Item, cancellationToken);
            }

            foreach (var machinery in automation)
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }

            var now = time.GetUtcNow();
            var farms = await OpenAsync(x, y, now, cancellationToken);
            var farm = Find(farms, id);
            farm.Patch.Advance(now);
            if (kind is not null)
            {
                farm.Patch.Sow(kind);
            }

            if (manure > 0)
            {
                var added = farm.Patch.AddManure(manure);
                if (added < manure)
                {
                    throw new GameException("farmstead_full", $"A farmstead keeps at most {Farmsteads.MaxManure} manure.");
                }

                await inventory.RemoveAsync(player, Items.Manure, added, cancellationToken);
            }

            await SaveAsync(x, y, farms, cancellationToken);
            return await ReportAsync(x, y, cancellationToken);
        }, cancellationToken);

    private static FarmsteadOnPlot Find(IEnumerable<FarmsteadOnPlot> farms, Guid id) =>
        farms.FirstOrDefault(f => f.Object.Id == id) ?? throw new GameException("no_object", "There is no such farmstead on this plot.");

    public async Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken)
    {
        if (!Items.IsFarmstead(placed.Kind))
        {
            return;
        }

        await using (var insert = await db.CommandAsync(
            """
            INSERT INTO world.plot_farmsteads (object_id, x, y, owner_id, crop, store, manure, busy_until)
            VALUES (@id, @x, @y, @owner, @crop, '{}', 0, @now) ON CONFLICT DO NOTHING
            """, cancellationToken))
        {
            insert.Parameters.AddWithValue("id", placed.Id);
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("owner", owner);
            insert.Parameters.AddWithValue("crop", Farming.Crops[0].Item);
            insert.Parameters.AddWithValue("now", time.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        // It tills and sows its fields at once.
        await CatchUpAsync(x, y, cancellationToken);
    }

    public async Task CatchUpAsync(int x, int y, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var farms = await OpenAsync(x, y, now, cancellationToken);
        foreach (var farm in farms)
        {
            farm.Patch.Advance(now);
        }

        await SaveAsync(x, y, farms, cancellationToken);
    }

    /// <summary>The farmstead is being picked up (worked up to now just before): its crops and manure go to the player, its fields go with it.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using (var fields = await db.CommandAsync("DELETE FROM world.plot_fields WHERE farmstead_id = @id", cancellationToken))
        {
            fields.Parameters.AddWithValue("id", id);
            await fields.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var delete = await db.CommandAsync("DELETE FROM world.plot_farmsteads WHERE object_id = @id RETURNING store, manure", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        var back = new List<(string Item, int Quantity)>();
        await using (var reader = await delete.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                back.AddRange(ReadStore(reader.GetString(0)).Select(s => (s.Key, s.Value)));
                back.Add((Items.Manure, reader.GetInt32(1)));
            }
        }

        foreach (var (item, quantity) in back.Where(b => b.Quantity > 0))
        {
            await inventory.AddAsync(owner, item, quantity, cancellationToken);
        }
    }

    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (!Items.IsFarmstead(kind) || await objects.PlotOfAsync(id, cancellationToken) is not var (x, y))
        {
            return null;
        }

        var farms = _prepared is { } prepared && prepared.X == x && prepared.Y == y ? prepared.Opened : await OpenAsync(x, y, null, cancellationToken);
        return farms.FirstOrDefault(f => f.Object.Id == id) is { } farm ? new Port(this, x, y, farms, farm.Patch) : null;
    }

    /// <summary>The plot's farmsteads opened once for all the ports of one run of its logistics, as opening them works out the whole plot.</summary>
    private (int X, int Y, List<FarmsteadOnPlot> Opened)? _prepared;

    public async Task PrepareAsync(IReadOnlyList<PlotObject> reached, CancellationToken cancellationToken)
    {
        _prepared = null;
        if (reached.FirstOrDefault(o => Items.IsFarmstead(o.Kind)) is { } first && await objects.PlotOfAsync(first.Id, cancellationToken) is var (x, y))
        {
            _prepared = (x, y, await OpenAsync(x, y, null, cancellationToken));
        }
    }

    public async Task<IReadOnlyList<MachineStatus>> StatusesAsync(Guid owner, CancellationToken cancellationToken)
    {
        var plotsOf = new List<(int X, int Y)>();
        await using (var command = await db.CommandAsync("SELECT DISTINCT x, y FROM world.plot_farmsteads WHERE owner_id = @owner", cancellationToken))
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

    /// <summary>The plot's farmsteads, only <paramref name="owner"/>'s when given.</summary>
    private async Task<IReadOnlyList<MachineStatus>> OnPlotAsync(int x, int y, Guid? owner, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var farms = await OpenAsync(x, y, now, cancellationToken);
        foreach (var farm in farms)
        {
            farm.Patch.Advance(now);
        }

        await SaveAsync(x, y, farms, cancellationToken);
        return farms.Where(f => owner is null || f.Object.Owner == owner).Select(f => new MachineStatus(
            f.Object.Id, f.Object.Kind, f.Patch.Crop.Item, f.Patch.Full ? MachineActivity.OutputFull : MachineActivity.Working, [])).ToList();
    }

    private static Dictionary<string, int> ReadStore(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(json) is { } store ? store.Where(s => Items.Exists(s.Key)).ToDictionary() : [];

    /// <summary>The farmsteads of a plot with their fields, locked until the transaction ends.</summary>
    private async Task<List<FarmsteadOnPlot>> OpenAsync(int x, int y, DateTimeOffset? now, CancellationToken cancellationToken)
    {
        var all = await objects.ObjectsAsync(x, y, cancellationToken);
        var farmsteads = all.Where(o => Items.IsFarmstead(o.Kind)).GroupBy(o => o.Id).Select(g => g.First()).ToList();
        if (farmsteads.Count == 0)
        {
            return [];
        }

        var rows = new Dictionary<Guid, (string Crop, string Store, int Manure, DateTimeOffset BusyUntil)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT object_id, crop, store, manure, busy_until FROM world.plot_farmsteads
            WHERE x = @x AND y = @y ORDER BY object_id FOR UPDATE
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows[reader.GetGuid(0)] = (reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetFieldValue<DateTimeOffset>(4));
            }
        }

        var biome = map.BiomeAt(x, y);
        var tiles = await plots.TilesAsync(x, y, biome, cancellationToken);
        var taken = new HashSet<(int, int)>();
        foreach (var o in all)
        {
            taken.UnionWith(Footprints.Tiles(o.Kind, o.TileX, o.TileY));
        }

        foreach (var table in new[] { "plot_trees", "plot_animals" })
        {
            await using var command = await db.CommandAsync($"SELECT tile_x, tile_y FROM world.{table} WHERE x = @x AND y = @y", cancellationToken);
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                taken.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
        }

        // Fields: its own, and the player's tilled by hand, which it leaves alone.
        var fields = new Dictionary<(int, int), (Guid? Farmstead, string? Crop, DateTimeOffset? PlantedAt)>();
        await using (var command = await db.CommandAsync(
            "SELECT tile_x, tile_y, farmstead_id, crop, planted_at FROM world.plot_fields WHERE x = @x AND y = @y", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                fields[(reader.GetInt32(0), reader.GetInt32(1))] = (reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
            }
        }

        taken.Add(PlotLayout.Spawn);
        var areas = Farmsteads.Areas(farmsteads);
        var start = now ?? time.GetUtcNow();
        var cropSpeed = farmsteads.Count > 0 ? (await smog.LevelAsync(x, y, cancellationToken)).CropSpeed : 1;
        var result = new List<FarmsteadOnPlot>();
        foreach (var o in farmsteads)
        {
            var level = Farmsteads.Level(o.Kind)!;
            // A sprinkler within its square waters all its fields.
            if (all.Any(s => s.Kind == Items.Sprinkler && Farmsteads.Claims(level, s.TileX - o.TileX, s.TileY - o.TileY)))
            {
                level = level with { GrowSpeed = level.GrowSpeed * Farming.SprinklerSpeed };
            }

            var (crop, store, manure, busyUntil) = rows.GetValueOrDefault(o.Id, (Farming.Crops[0].Item, "{}", 0, start));
            var patch = new FarmPatch(level, Farming.ById.GetValueOrDefault(crop) ?? Farming.Crops[0], ReadStore(store), manure, busyUntil, cropSpeed);
            foreach (var tile in areas[o.Id])
            {
                if (fields.TryGetValue(tile, out var field))
                {
                    if (field.Farmstead != o.Id)
                    {
                        continue;
                    }

                    if (field.Crop is { } grows && Farming.ById.TryGetValue(grows, out var info) && field.PlantedAt is { } planted)
                    {
                        patch.Fields[tile] = (info, planted + info.Grow);
                    }
                    else
                    {
                        patch.Bare.Add(tile);
                    }
                }
                else if (!taken.Contains(tile) && tiles[tile.Y * PlotLayout.Size + tile.X] == (byte)TileObject.None)
                {
                    patch.Open.Add(tile);
                }
            }

            result.Add(new FarmsteadOnPlot(o, patch));
        }

        return result;
    }

    /// <summary>Writes back the farmsteads that worked: their store, manure, crop and the fields they sowed.</summary>
    private async Task SaveAsync(int x, int y, IReadOnlyList<FarmsteadOnPlot> farms, CancellationToken cancellationToken)
    {
        foreach (var (o, patch) in farms.Where(f => f.Patch.Changed).Select(f => (f.Object, f.Patch)))
        {
            await using (var update = await db.CommandAsync(
                """
                INSERT INTO world.plot_farmsteads (object_id, x, y, owner_id, crop, store, manure, busy_until)
                VALUES (@id, @x, @y, @owner, @crop, @store, @manure, @busy)
                ON CONFLICT (object_id) DO UPDATE SET crop = @crop, store = @store, manure = @manure, busy_until = @busy
                """, cancellationToken))
            {
                update.Parameters.AddWithValue("id", o.Id);
                update.Parameters.AddWithValue("x", x);
                update.Parameters.AddWithValue("y", y);
                update.Parameters.AddWithValue("owner", o.Owner);
                update.Parameters.AddWithValue("crop", patch.Crop.Item);
                update.Parameters.AddWithValue("store", JsonSerializer.Serialize(patch.Store));
                update.Parameters.AddWithValue("manure", patch.Manure);
                update.Parameters.AddWithValue("busy", patch.BusyUntil);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            if (patch.Sown.Count > 0)
            {
                // A field stores when it would have been sown by hand to be ripe when it is: ripe time less the crop's own growing time.
                var sown = patch.Sown.ToList();
                await using var fields = await db.CommandAsync(
                    """
                    INSERT INTO world.plot_fields (x, y, tile_x, tile_y, crop, planted_at, farmstead_id)
                    SELECT @x, @y, t.tx, t.ty, t.crop, t.planted, @farmstead FROM unnest(@txs, @tys, @crops, @planted) AS t(tx, ty, crop, planted)
                    ON CONFLICT (x, y, tile_x, tile_y) DO UPDATE
                    SET crop = EXCLUDED.crop, planted_at = EXCLUDED.planted_at, farmstead_id = EXCLUDED.farmstead_id
                    """, cancellationToken);
                fields.Parameters.AddWithValue("farmstead", o.Id);
                fields.Parameters.AddWithValue("x", x);
                fields.Parameters.AddWithValue("y", y);
                fields.Parameters.AddWithValue("txs", sown.Select(t => t.X).ToArray());
                fields.Parameters.AddWithValue("tys", sown.Select(t => t.Y).ToArray());
                fields.Parameters.AddWithValue("crops", sown.Select(t => patch.Fields[t].Crop.Item).ToArray());
                fields.Parameters.Add(new Npgsql.NpgsqlParameter("planted", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
                {
                    Value = sown.Select(t => patch.Fields[t].ReadyAt - patch.Fields[t].Crop.Grow).ToArray(),
                });
                await fields.ExecuteNonQueryAsync(cancellationToken);
                patch.Sown.Clear();
            }

            if (patch.Made.Count > 0)
            {
                await stats.RecordAsync(o.Owner, patch.Made.ToList(), cancellationToken);
                patch.Made.Clear();
            }
        }
    }

    private async Task<IReadOnlyList<FarmsteadOnPlot>> ReportAsync(int x, int y, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var farms = await OpenAsync(x, y, now, cancellationToken);
        foreach (var farm in farms)
        {
            farm.Patch.Advance(now);
        }

        await SaveAsync(x, y, farms, cancellationToken);
        return farms;
    }

    /// <summary>A farmstead as inserters see it: crops come out, manure goes in.</summary>
    private sealed class Port(FarmsteadService service, int x, int y, List<FarmsteadOnPlot> farms, FarmPatch patch) : IMachinePort
    {
        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            patch.Advance(now);
            return patch.Store.Keys.Order().FirstOrDefault(wanted);
        }

        public void Take(string item, DateTimeOffset now)
        {
            patch.Advance(now);
            if (patch.Take(1, now, item) == 1)
            {
                patch.Advance(now);
            }
        }

        public bool Accepts(string item, DateTimeOffset now) => item == Items.Manure && patch.Manure < Farmsteads.MaxManure;

        public void Put(string item, DateTimeOffset now)
        {
            patch.Advance(now);
            patch.AddManure(1);
        }

        public DateTimeOffset? NextChange(DateTimeOffset now)
        {
            patch.Advance(now);
            return patch.Full ? null : patch.NextReap();
        }

        public Task SaveAsync(CancellationToken cancellationToken) =>
            service.SaveAsync(x, y, farms.Where(f => f.Patch == patch).ToList(), cancellationToken);
    }
}

public sealed record FarmsteadsRequest(int X, int Y);

public sealed record FarmsteadRequest(int X, int Y, Guid Id, string? Crop = null, int Manure = 0);

public abstract class FarmsteadHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var request = Payload.Read<FarmsteadsRequest>(payload);
        var (state, taken, xp) = await RunAsync(
            scope.ServiceProvider.GetRequiredService<FarmsteadService>(), scope.ServiceProvider.GetServices<IPlotAutomation>(),
            client.RequireAccount(), payload, cancellationToken);
        var now = time.GetUtcNow();
        return new Reply("farmsteads", new
        {
            x = request.X,
            y = request.Y,
            farmsteads = state.Select(f => FarmsteadJson(f, now)),
            // The fields they sow, to draw on the plot: unix milliseconds of when each is ripe.
            fields = state.SelectMany(f => f.Patch.Fields.Select(t => new
            {
                tileX = t.Key.X,
                tileY = t.Key.Y,
                crop = t.Value.Crop.Item,
                readyAt = t.Value.ReadyAt.ToUnixTimeMilliseconds(),
                farmstead = f.Object.Id,
            })),
            taken = taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = xp is { } gain ? new { xp = gain.Xp, level = gain.Level, leveledUp = gain.LeveledUp } : null,
            serverTime = now.ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<(IReadOnlyList<FarmsteadOnPlot>, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        FarmsteadService farmsteads, IEnumerable<IPlotAutomation> automation, Guid player, JsonElement? payload, CancellationToken cancellationToken);

    public static object FarmsteadJson(FarmsteadOnPlot farm, DateTimeOffset now)
    {
        var (o, patch) = (farm.Object, farm.Patch);
        var level = patch.FarmLevel;
        var next = Farmsteads.Levels.FirstOrDefault(l => l.Level == level.Level + 1);
        return new
        {
            id = o.Id,
            kind = o.Kind,
            name = Items.Names[o.Kind],
            level = level.Level,
            tileX = o.TileX,
            tileY = o.TileY,
            rotation = o.Rotation,
            radius = level.Radius,
            clearing = Farmsteads.Clearing,
            crops = patch.Crops,
            store = patch.Store.OrderBy(s => s.Key).Select(s => new { id = s.Key, name = Items.Names[s.Key], quantity = s.Value }),
            capacity = level.Capacity,
            reapSeconds = level.ReapTime.TotalSeconds,
            crop = patch.Crop.Item,
            manure = patch.Manure,
            maxManure = Farmsteads.MaxManure,
            // A sprinkler within its square waters its fields: its crops grow faster.
            watered = level.GrowSpeed > (Farmsteads.Level(o.Kind)?.GrowSpeed ?? level.GrowSpeed),
            kinds = Farming.Crops.Select(c => new
            {
                id = c.Item,
                name = Items.Names[c.Item],
                growSeconds = Farmsteads.GrowTime(level, c, false).TotalSeconds,
                manureSeconds = Farmsteads.GrowTime(level, c, true).TotalSeconds,
                yield = c.Yield,
                growing = patch.Fields.Values.Count(f => f.Crop == c),
            }),
            fields = patch.Fields.Count,
            ripe = patch.Fields.Values.Count(f => f.ReadyAt <= now),
            full = patch.Full,
            upgrade = next is null ? null : new
            {
                kind = next.Kind,
                name = Items.Names[next.Kind],
                radius = next.Radius,
                capacity = next.Capacity,
                reapSeconds = next.ReapTime.TotalSeconds,
                growSpeed = next.GrowSpeed,
            },
        };
    }
}

/// <summary>The plot's farmsteads worked up to now; the client asks every few seconds.</summary>
public sealed class GetFarmsteadsHandler(IServiceScopeFactory scopes, TimeProvider time) : FarmsteadHandler(scopes, time)
{
    public override string Type => "farmstead.get";

    protected override async Task<(IReadOnlyList<FarmsteadOnPlot>, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        FarmsteadService farmsteads, IEnumerable<IPlotAutomation> automation, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<FarmsteadsRequest>(payload);
        return (await farmsteads.StateAsync(player, request.X, request.Y, automation, cancellationToken), [], null);
    }
}

/// <summary>Takes all the crops out of a farmstead into the inventory.</summary>
public sealed class TakeFromFarmsteadHandler(IServiceScopeFactory scopes, TimeProvider time) : FarmsteadHandler(scopes, time)
{
    public override string Type => "farmstead.take";

    protected override Task<(IReadOnlyList<FarmsteadOnPlot>, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        FarmsteadService farmsteads, IEnumerable<IPlotAutomation> automation, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<FarmsteadRequest>(payload);
        return farmsteads.TakeAsync(player, request.X, request.Y, request.Id, automation, cancellationToken);
    }
}

/// <summary>Picks the crop a farmstead sows from now on and puts manure into it.</summary>
public sealed class SowFarmsteadHandler(IServiceScopeFactory scopes, TimeProvider time) : FarmsteadHandler(scopes, time)
{
    public override string Type => "farmstead.sow";

    protected override async Task<(IReadOnlyList<FarmsteadOnPlot>, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        FarmsteadService farmsteads, IEnumerable<IPlotAutomation> automation, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<FarmsteadRequest>(payload);
        return (await farmsteads.SowAsync(player, request.X, request.Y, request.Id, request.Crop, request.Manure, automation, cancellationToken), [], null);
    }
}
