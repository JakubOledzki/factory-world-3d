using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>How a forester's building of one level works.</summary>
/// <param name="Radius">Its forest reaches this many tiles from its own tile each way: a square of 2 × Radius + 1.</param>
/// <param name="Capacity">Most wood it keeps; full, it stops cutting until wood is taken out.</param>
/// <param name="GrowSpeed">How many times faster than on its own a tree grows in its care.</param>
/// <param name="CutTime">How long it takes to cut one tree; it cuts one after another.</param>
public sealed record ForesterLevel(string Kind, int Level, int Radius, int Capacity, double GrowSpeed, TimeSpan CutTime);

/// <summary>
/// A forester's building stands on 3 × 3 tiles of a plot and keeps a forest in the square around it:
/// it plants a sapling on every free tile there, cuts each tree once it has grown and plants a new one
/// in its place. Trees that grew there by themselves it cuts first. It keeps a clearing one tile wide
/// around itself and a path three tiles wide from its door (the way it faces) to the edge of its forest,
/// so the player can walk up to it and belts can carry the wood away. The wood waits in it until taken out
/// by hand or by an inserter standing next to it. The player picks the kind of tree it plants (a pine at first):
/// what grows already stays and is cut as it is, and the new kind goes in where the trees stood. An oak forest gives
/// oak wood, the rest plain wood. The lodge and the forestry station are the same, only with a bigger forest that
/// grows faster, quicker cutting and room for more wood.
/// </summary>
public static class Foresters
{
    public static readonly IReadOnlyList<ForesterLevel> Levels =
    [
        new(Items.ForesterHut, 1, 5, 100, 1, TimeSpan.FromSeconds(6)),
        new(Items.ForesterLodge, 2, 6, 200, 1.5, TimeSpan.FromSeconds(3)),
        new(Items.ForestryStation, 3, 7, 400, 2, TimeSpan.FromSeconds(1.5)),
    ];

    public static ForesterLevel? Level(string kind) => Levels.FirstOrDefault(l => l.Kind == kind);

    /// <summary>The clearing around the building reaches this far from its tile: the building itself and one tile more.</summary>
    public const int Clearing = 2;

    /// <summary>Wood taken out by hand gives one experience point for every this many.</summary>
    public const int WoodPerXp = 4;

    /// <summary>A tree nursery raises the saplings of the foresters whose buildings stand this many tiles from it each way.</summary>
    public const int NurseryReach = 8;

    /// <summary>How many times faster a forest grows from a nursery's saplings.</summary>
    public const double NurserySpeed = 1.5;

    /// <summary>Whether a tree nursery among <paramref name="objects"/> raises the saplings of a forester standing on (tileX, tileY).</summary>
    public static bool Nursed(IEnumerable<PlotObject> objects, int tileX, int tileY) =>
        objects.Any(o => o.Kind == Items.TreeNursery && Math.Abs(o.TileX - tileX) <= NurseryReach && Math.Abs(o.TileY - tileY) <= NurseryReach);

    /// <summary>How long a sapling of <paramref name="kind"/> (a pine's when none) grows in its care on a plot of <paramref name="biome"/>;
    /// null where nothing grows.</summary>
    public static TimeSpan? GrowTime(ForesterLevel level, Biome biome, TreeKind? kind = null) =>
        Trees.GrowTime(biome, kind ?? Trees.Kinds[0]) / level.GrowSpeed;

    /// <summary>The way the door faces, as (dx, dy): 0 south, 1 east, 2 north, 3 west.</summary>
    public static (int Dx, int Dy) Facing(int rotation) => (rotation & 3) switch
    {
        0 => (0, 1),
        1 => (1, 0),
        2 => (0, -1),
        _ => (-1, 0),
    };

    /// <summary>Whether it plants on the tile (dx, dy) away from its own: inside its square, outside its clearing and path.</summary>
    public static bool Plants(ForesterLevel level, int rotation, int dx, int dy)
    {
        if (!Claims(level, dx, dy) || (Math.Abs(dx) <= Clearing && Math.Abs(dy) <= Clearing))
        {
            return false;
        }

        var (fx, fy) = Facing(rotation);
        return !(dx * fx + dy * fy > 0 && Math.Abs(dx * fy - dy * fx) <= Footprints.Reach(level.Kind));
    }

    /// <summary>Whether the tile (dx, dy) away from its own is in its square at all.</summary>
    public static bool Claims(ForesterLevel level, int dx, int dy) => Math.Abs(dx) <= level.Radius && Math.Abs(dy) <= level.Radius;

    /// <summary>
    /// The tiles of the plot each forester plants on. Where the squares of two meet, a tile belongs to the
    /// one that comes first from north to south, west to east; no forester plants on another's clearing or path.
    /// </summary>
    public static IReadOnlyDictionary<Guid, List<(int X, int Y)>> Areas(IReadOnlyList<PlotObject> foresters)
    {
        var ordered = foresters.OrderBy(f => f.TileY).ThenBy(f => f.TileX).ThenBy(f => f.Id).ToList();
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
                    if (!PlotLayout.Inside(x, y) || !Plants(level, f.Rotation, dx, dy) || taken.Contains((x, y)))
                    {
                        continue;
                    }

                    var kept = ordered.Any(o => o.Id != f.Id && Level(o.Kind) is { } other
                        && Claims(other, x - o.TileX, y - o.TileY) && !Plants(other, o.Rotation, x - o.TileX, y - o.TileY));
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

/// <summary>
/// One forester's forest as it works through time, in memory: worked out up to a moment from the trees
/// standing in it, then written back. Times only ever move forward.
/// </summary>
/// <param name="grow">How long a sapling of a kind grows in its care.</param>
/// <param name="species">The kind of tree it plants.</param>
/// <param name="store">The wood it keeps, by item.</param>
/// <param name="busyUntil">When it is done with the tree it cuts now, or could start the next one.</param>
public sealed class ForestPatch(
    ForesterLevel level, Func<TreeKind, TimeSpan> grow, TreeKind species, IReadOnlyDictionary<string, int> store, DateTimeOffset busyUntil)
{
    public ForestPatch(ForesterLevel level, TimeSpan grow, int wood, DateTimeOffset busyUntil)
        : this(level, _ => grow, World.Trees.Kinds[0], new Dictionary<string, int> { [Items.Wood] = wood }, busyUntil)
    {
    }

    public ForesterLevel ForesterLevel { get; } = level;

    /// <summary>The kind of tree it plants on free tiles and where it cut one.</summary>
    public TreeKind Species { get; private set; } = species;

    /// <summary>The wood it keeps, by item: plain wood and oak wood.</summary>
    public Dictionary<string, int> Store { get; } = store.Where(s => s.Value > 0).ToDictionary();

    /// <summary>All the wood it keeps, of every kind.</summary>
    public int Wood => Store.Values.Sum();

    public DateTimeOffset BusyUntil { get; private set; } = busyUntil;

    /// <summary>Planted trees in its forest and when each has grown.</summary>
    public Dictionary<(int X, int Y), DateTimeOffset> Trees { get; } = [];

    /// <summary>The kind of each planted tree; a pine where none is set.</summary>
    public Dictionary<(int X, int Y), TreeKind> Kinds { get; } = [];

    /// <summary>Trees that grew in its forest by themselves and stand there still, with their kind.</summary>
    public Dictionary<(int X, int Y), TreeKind> Wild { get; } = [];

    /// <summary>Free tiles of its forest, to be planted the next time it works.</summary>
    public HashSet<(int X, int Y)> Open { get; } = [];

    /// <summary>Tiles whose tree it planted, as rows to write.</summary>
    public HashSet<(int X, int Y)> Planted { get; } = [];

    /// <summary>Tiles whose wild tree it cut, to be marked as gathered.</summary>
    public HashSet<(int X, int Y)> Cleared { get; } = [];

    /// <summary>Wood it cut, for the production statistics.</summary>
    public List<ProductionEntry> Made { get; } = [];

    public bool Changed { get; private set; }

    public bool Full => Wood >= ForesterLevel.Capacity;

    /// <summary>The kind of the tree on a tile of its forest.</summary>
    public TreeKind KindAt((int X, int Y) tile) =>
        Wild.GetValueOrDefault(tile) ?? Kinds.GetValueOrDefault(tile) ?? World.Trees.Kinds[0];

    /// <summary>From now on it plants <paramref name="kind"/>; the trees standing are cut as they are.</summary>
    public void Plant(TreeKind kind)
    {
        if (kind != Species)
        {
            Species = kind;
            Changed = true;
        }
    }

    private void PlantAt((int X, int Y) tile, DateTimeOffset at)
    {
        Trees[tile] = at + grow(Species);
        Kinds[tile] = Species;
        Planted.Add(tile);
    }

    /// <summary>Plants the free tiles at <paramref name="now"/>, then cuts every tree it could have cut by then.</summary>
    public void Advance(DateTimeOffset now)
    {
        foreach (var tile in Open)
        {
            PlantAt(tile, now);
            Changed = true;
        }

        Open.Clear();
        if (Full || NextCut() is not { } next || next > now)
        {
            return;
        }

        // Wild trees are ready from the start, so they come first.
        var queue = new PriorityQueue<(int X, int Y), DateTimeOffset>();
        foreach (var tile in Wild.Keys)
        {
            queue.Enqueue(tile, DateTimeOffset.MinValue);
        }

        foreach (var (tile, ready) in Trees)
        {
            if (ready + ForesterLevel.CutTime <= now)
            {
                queue.Enqueue(tile, ready);
            }
        }

        while (!Full && queue.TryPeek(out var tile, out var ready))
        {
            var done = (ready > BusyUntil ? ready : BusyUntil) + ForesterLevel.CutTime;
            if (done > now)
            {
                break;
            }

            queue.Dequeue();
            var kind = KindAt(tile);
            // What does not fit in the store is left in the forest.
            var cut = Math.Min(kind.WoodQuantity, ForesterLevel.Capacity - Wood);
            Store[kind.Wood] = Store.GetValueOrDefault(kind.Wood) + cut;
            BusyUntil = done;
            Made.Add(ProductionEntry.Made(kind.Wood, cut, done));
            if (Wild.Remove(tile))
            {
                Cleared.Add(tile);
            }

            // A new sapling of the kind it plants now goes in at once where the tree stood.
            PlantAt(tile, done);
            var grown = Trees[tile];
            if (grown + ForesterLevel.CutTime <= now)
            {
                queue.Enqueue(tile, grown);
            }
        }

        Changed = true;
    }

    /// <summary>When it will next have a tree cut, if it has room for the wood; null when its forest is empty.</summary>
    public DateTimeOffset? NextCut()
    {
        DateTimeOffset? first = Wild.Count > 0 ? DateTimeOffset.MinValue : null;
        foreach (var ready in Trees.Values)
        {
            if (first is null || ready < first)
            {
                first = ready;
            }
        }

        return first is { } at ? (at > BusyUntil ? at : BusyUntil) + ForesterLevel.CutTime : null;
    }

    /// <summary>Takes wood out at <paramref name="now"/>, plain wood first unless <paramref name="item"/> is named;
    /// one that stood full starts cutting again only from then.</summary>
    public int Take(int quantity, DateTimeOffset now, string? item = null)
    {
        var taken = 0;
        foreach (var kind in Store.Keys.Where(k => item is null || k == item).OrderBy(k => k == Items.Wood ? 0 : 1).ToList())
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
}

/// <summary>A forester on its plot with its forest.</summary>
public sealed record ForesterOnPlot(PlotObject Object, ForestPatch Patch);

/// <summary>All the foresters of one plot, read and locked until the transaction ends.</summary>
public sealed record PlotForests(int X, int Y, IReadOnlyList<ForesterOnPlot> Foresters, IReadOnlyList<PlantedTree> Trees, IReadOnlyList<(int X, int Y)> Cleared);

public sealed class ForesterService(
    GameDb db, WorldMap map, PlotService plots, IPlotObjects objects, IInventory inventory, ICharacterProgress progress, IProductionStats stats,
    TimeProvider time) : IPlacedHook, IPlotAutomation, IMachineContents, IMachinePorts, IMachineStatuses
{
    /// <summary>The plot's foresters worked up to now, with every planted tree and the wild trees they cut.</summary>
    public Task<PlotForests> StateAsync(Guid player, int x, int y, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            // Inserters take wood out of foresters; they go first so the wood they took is gone.
            foreach (var machinery in automation)
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }

            return await ReportAsync(x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes the wood out of a forester on the player's plot into the inventory, as far as it fits; the rest stays.</summary>
    public Task<(PlotForests State, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp)> TakeAsync(
        Guid player, int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            foreach (var machinery in automation)
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }

            var now = time.GetUtcNow();
            var forests = await OpenAsync(x, y, now, cancellationToken);
            var forester = forests.FirstOrDefault(f => f.Object.Id == id)
                ?? throw new GameException("no_object", "There is no such forester's building on this plot.");
            var taken = new List<(string Item, int Quantity)>();
            foreach (var (item, quantity) in forester.Patch.Store.OrderBy(s => s.Key == Items.Wood ? 0 : 1).ToList())
            {
                var fits = (int)await inventory.RoomForAsync(player, item, quantity, cancellationToken);
                if (fits > 0)
                {
                    forester.Patch.Take(fits, now, item);
                    await inventory.AddAsync(player, item, fits, cancellationToken);
                    taken.Add((item, fits));
                }
            }

            if (taken.Count == 0 && forester.Patch.Store.Count > 0)
            {
                throw InventoryExtensions.InventoryFull();
            }

            // Room again: what waited on the full store is cut from now on.
            forester.Patch.Advance(now);
            await SaveAsync(x, y, forests, cancellationToken);
            XpGain? xp = null;

            if (taken.Count > 0)
            {
                xp = await progress.AddXpAsync(player, taken.Sum(t => t.Quantity) / Foresters.WoodPerXp, cancellationToken);
            }

            return (await ReportAsync(x, y, cancellationToken), (IReadOnlyList<(string, int)>)taken, xp);
        }, cancellationToken);

    /// <summary>Sets the kind of tree a forester on the player's plot plants from now on.</summary>
    public Task<PlotForests> PlantAsync(
        Guid player, int x, int y, Guid id, string species, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var kind = Trees.ById.GetValueOrDefault(species) ?? throw new GameException("bad_species", "There is no such kind of tree.");
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            foreach (var machinery in automation)
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }

            var now = time.GetUtcNow();
            var forests = await OpenAsync(x, y, now, cancellationToken);
            var forester = forests.FirstOrDefault(f => f.Object.Id == id)
                ?? throw new GameException("no_object", "There is no such forester's building on this plot.");
            forester.Patch.Advance(now);
            forester.Patch.Plant(kind);
            await SaveAsync(x, y, forests, cancellationToken);
            return await ReportAsync(x, y, cancellationToken);
        }, cancellationToken);

    public async Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken)
    {
        if (!Items.IsForester(placed.Kind))
        {
            return;
        }

        await using (var insert = await db.CommandAsync(
            """
            INSERT INTO world.plot_foresters (object_id, x, y, owner_id, wood, busy_until)
            VALUES (@id, @x, @y, @owner, 0, @now) ON CONFLICT DO NOTHING
            """, cancellationToken))
        {
            insert.Parameters.AddWithValue("id", placed.Id);
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("owner", owner);
            insert.Parameters.AddWithValue("now", time.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        // It plants its forest at once.
        await CatchUpAsync(x, y, cancellationToken);
    }

    /// <summary>Works every forester of the plot up to now; called before anything is put on the plot or taken off.</summary>
    public async Task CatchUpAsync(int x, int y, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var forests = await OpenAsync(x, y, now, cancellationToken);
        foreach (var forester in forests)
        {
            forester.Patch.Advance(now);
        }

        await SaveAsync(x, y, forests, cancellationToken);
    }

    /// <summary>The forester is being picked up (worked up to now just before): its wood goes to the player,
    /// and the forest it planted goes with it.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using (var trees = await db.CommandAsync("DELETE FROM world.plot_trees WHERE forester_id = @id", cancellationToken))
        {
            trees.Parameters.AddWithValue("id", id);
            await trees.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var delete = await db.CommandAsync("DELETE FROM world.plot_foresters WHERE object_id = @id RETURNING wood, oak_wood", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        var store = new List<(string Item, int Quantity)>();
        await using (var reader = await delete.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                store.Add((Items.Wood, reader.GetInt32(0)));
                store.Add((Items.OakWood, reader.GetInt32(1)));
            }
        }

        foreach (var (item, quantity) in store.Where(s => s.Quantity > 0))
        {
            await inventory.AddAsync(owner, item, quantity, cancellationToken);
        }
    }

    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (!Items.IsForester(kind) || await objects.PlotOfAsync(id, cancellationToken) is not var (x, y))
        {
            return null;
        }

        // Inserters run from where the plot's logistics were left, so the forester starts from its last state.
        var forests = _prepared is { } prepared && prepared.X == x && prepared.Y == y ? prepared.Opened : await OpenAsync(x, y, null, cancellationToken);
        return forests.FirstOrDefault(f => f.Object.Id == id) is { } forester ? new Port(this, x, y, forests, forester.Patch) : null;
    }

    /// <summary>The plot's foresters opened once for all the ports of one run of its logistics, as opening them works out the whole plot.</summary>
    private (int X, int Y, List<ForesterOnPlot> Opened)? _prepared;

    public async Task PrepareAsync(IReadOnlyList<PlotObject> reached, CancellationToken cancellationToken)
    {
        _prepared = null;
        if (reached.FirstOrDefault(o => Items.IsForester(o.Kind)) is { } first && await objects.PlotOfAsync(first.Id, cancellationToken) is var (x, y))
        {
            _prepared = (x, y, await OpenAsync(x, y, null, cancellationToken));
        }
    }

    public async Task<IReadOnlyList<MachineStatus>> StatusesAsync(Guid owner, CancellationToken cancellationToken)
    {
        var plotsOf = new List<(int X, int Y)>();
        await using (var command = await db.CommandAsync(
            "SELECT DISTINCT x, y FROM world.plot_foresters WHERE owner_id = @owner", cancellationToken))
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

    /// <summary>The plot's foresters, only <paramref name="owner"/>'s when given.</summary>
    private async Task<IReadOnlyList<MachineStatus>> OnPlotAsync(int x, int y, Guid? owner, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var forests = await OpenAsync(x, y, now, cancellationToken);
        foreach (var forester in forests)
        {
            forester.Patch.Advance(now);
        }

        await SaveAsync(x, y, forests, cancellationToken);
        return forests.Where(f => owner is null || f.Object.Owner == owner).Select(f => new MachineStatus(
            f.Object.Id, f.Object.Kind, f.Patch.Species.Wood, f.Patch.Full ? MachineActivity.OutputFull : MachineActivity.Working, [])).ToList();
    }

    /// <summary>The foresters of a plot with their forests, locked until the transaction ends. A forester
    /// whose row is missing starts empty from <paramref name="now"/> (or the time now when null).</summary>
    private async Task<List<ForesterOnPlot>> OpenAsync(int x, int y, DateTimeOffset? now, CancellationToken cancellationToken)
    {
        var all = await objects.ObjectsAsync(x, y, cancellationToken);
        var foresters = all.Where(o => Items.IsForester(o.Kind)).ToList();
        if (foresters.Count == 0)
        {
            return [];
        }

        var rows = new Dictionary<Guid, (int Wood, int OakWood, DateTimeOffset BusyUntil, string Species)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT object_id, wood, oak_wood, busy_until, species FROM world.plot_foresters
            WHERE x = @x AND y = @y ORDER BY object_id FOR UPDATE
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows[reader.GetGuid(0)] = (reader.GetInt32(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4));
            }
        }

        var biome = map.BiomeAt(x, y);
        var tiles = await plots.TilesAsync(x, y, biome, cancellationToken);
        var trees = await PlantedAsync(x, y, cancellationToken);
        var taken = new HashSet<(int, int)>();
        foreach (var o in all)
        {
            taken.UnionWith(Footprints.Tiles(o.Kind, o.TileX, o.TileY));
        }

        foreach (var table in new[] { "plot_fields", "plot_animals" })
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

        taken.Add(PlotLayout.Spawn);
        var areas = Foresters.Areas(foresters);
        var start = now ?? time.GetUtcNow();
        var result = new List<ForesterOnPlot>();
        foreach (var o in foresters)
        {
            var level = Foresters.Level(o.Kind)!;
            if (Foresters.Nursed(all, o.TileX, o.TileY))
            {
                level = level with { GrowSpeed = level.GrowSpeed * Foresters.NurserySpeed };
            }

            var (wood, oakWood, busyUntil, species) = rows.GetValueOrDefault(o.Id, (0, 0, start, Trees.Pine));
            var store = new Dictionary<string, int> { [Items.Wood] = wood, [Items.OakWood] = oakWood };
            var patch = new ForestPatch(level, k => Foresters.GrowTime(level, biome, k) ?? Forestry.Grow, Trees.Of(species), store, busyUntil);
            foreach (var tile in areas[o.Id])
            {
                if (trees.TryGetValue(tile, out var tree))
                {
                    patch.Trees[tile] = tree.ReadyAt;
                    patch.Kinds[tile] = tree.Kind;
                }
                else if (taken.Contains(tile))
                {
                    continue;
                }
                else if (Trees.ByTile.GetValueOrDefault((TileObject)tiles[tile.Y * PlotLayout.Size + tile.X]) is { } wild)
                {
                    patch.Wild[tile] = wild;
                }
                else if (tiles[tile.Y * PlotLayout.Size + tile.X] == (byte)TileObject.None)
                {
                    patch.Open.Add(tile);
                }
            }

            result.Add(new ForesterOnPlot(o, patch));
        }

        return result;
    }

    private async Task<Dictionary<(int X, int Y), (DateTimeOffset ReadyAt, TreeKind Kind)>> PlantedAsync(int x, int y, CancellationToken cancellationToken)
    {
        var trees = new Dictionary<(int, int), (DateTimeOffset, TreeKind)>();
        await using var command = await db.CommandAsync(
            "SELECT tile_x, tile_y, ready_at, species FROM world.plot_trees WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            trees[(reader.GetInt32(0), reader.GetInt32(1))] = (reader.GetFieldValue<DateTimeOffset>(2), Trees.Of(reader.GetString(3)));
        }

        return trees;
    }

    /// <summary>Writes back the foresters that worked: their wood, the trees they planted, the wild ones they cut.</summary>
    private async Task SaveAsync(int x, int y, IReadOnlyList<ForesterOnPlot> forests, CancellationToken cancellationToken)
    {
        foreach (var (o, patch) in forests.Where(f => f.Patch.Changed).Select(f => (f.Object, f.Patch)))
        {
            await using (var update = await db.CommandAsync(
                """
                INSERT INTO world.plot_foresters (object_id, x, y, owner_id, wood, oak_wood, busy_until, species)
                VALUES (@id, @x, @y, @owner, @wood, @oak, @busy, @species)
                ON CONFLICT (object_id) DO UPDATE SET wood = @wood, oak_wood = @oak, busy_until = @busy, species = @species
                """, cancellationToken))
            {
                update.Parameters.AddWithValue("id", o.Id);
                update.Parameters.AddWithValue("x", x);
                update.Parameters.AddWithValue("y", y);
                update.Parameters.AddWithValue("owner", o.Owner);
                update.Parameters.AddWithValue("wood", patch.Store.GetValueOrDefault(Items.Wood));
                update.Parameters.AddWithValue("oak", patch.Store.GetValueOrDefault(Items.OakWood));
                update.Parameters.AddWithValue("busy", patch.BusyUntil);
                update.Parameters.AddWithValue("species", patch.Species.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            if (patch.Planted.Count > 0)
            {
                var planted = patch.Planted.ToList();
                await using var trees = await db.CommandAsync(
                    """
                    INSERT INTO world.plot_trees (x, y, tile_x, tile_y, ready_at, forester_id, species)
                    SELECT @x, @y, t.tx, t.ty, t.ready, @forester, t.species FROM unnest(@txs, @tys, @readies, @species) AS t(tx, ty, ready, species)
                    ON CONFLICT (x, y, tile_x, tile_y) DO UPDATE
                    SET ready_at = EXCLUDED.ready_at, forester_id = EXCLUDED.forester_id, species = EXCLUDED.species
                    """, cancellationToken);
                trees.Parameters.AddWithValue("forester", o.Id);
                trees.Parameters.AddWithValue("x", x);
                trees.Parameters.AddWithValue("y", y);
                trees.Parameters.AddWithValue("txs", planted.Select(t => t.X).ToArray());
                trees.Parameters.AddWithValue("tys", planted.Select(t => t.Y).ToArray());
                trees.Parameters.Add(new Npgsql.NpgsqlParameter("readies", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
                {
                    Value = planted.Select(t => patch.Trees[t]).ToArray(),
                });
                trees.Parameters.AddWithValue("species", planted.Select(t => patch.KindAt(t).Id).ToArray());
                await trees.ExecuteNonQueryAsync(cancellationToken);
                patch.Planted.Clear();
            }

            if (patch.Cleared.Count > 0)
            {
                await PlotHarvests.MarkManyAsync(db, x, y, patch.Cleared.ToList(), at: null, cancellationToken);
                patch.Cleared.Clear();
            }

            if (patch.Made.Count > 0)
            {
                await stats.RecordAsync(o.Owner, patch.Made.ToList(), cancellationToken);
                patch.Made.Clear();
            }
        }
    }

    /// <summary>The foresters as they stand now, every planted tree of the plot and the wild trees cut in their squares.</summary>
    private async Task<PlotForests> ReportAsync(int x, int y, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var forests = await OpenAsync(x, y, now, cancellationToken);
        foreach (var forester in forests)
        {
            forester.Patch.Advance(now);
        }

        await SaveAsync(x, y, forests, cancellationToken);
        var trees = await ForestService.ReadAsync(db, x, y, cancellationToken);
        var cleared = new List<(int, int)>();
        if (forests.Count > 0)
        {
            var natural = PlotLayout.Generate(x, y, map.BiomeAt(x, y));
            await using var command = await db.CommandAsync("SELECT square, harvested_at FROM world.plot_harvests WHERE x = @x AND y = @y", cancellationToken);
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                foreach (var (tileX, tileY, _) in PlotHarvests.Gathered(reader.GetInt16(0), reader.GetFieldValue<DateTimeOffset?[]>(1)))
                {
                    var tile = (X: tileX, Y: tileY);
                    if (Trees.IsTree(natural[tile.Y * PlotLayout.Size + tile.X])
                        && forests.Any(f => Foresters.Claims(f.Patch.ForesterLevel, tile.X - f.Object.TileX, tile.Y - f.Object.TileY)))
                    {
                        cleared.Add(tile);
                    }
                }
            }
        }

        return new PlotForests(x, y, forests, trees, cleared);
    }

    /// <summary>A forester as inserters see it: wood comes out, nothing goes in. The other foresters of the
    /// plot are read with it, so the tiles where their squares meet are shared out the same way.</summary>
    private sealed class Port(ForesterService service, int x, int y, List<ForesterOnPlot> forests, ForestPatch patch) : IMachinePort
    {
        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            patch.Advance(now);
            return patch.Store.Keys.OrderBy(k => k == Items.Wood ? 0 : 1).FirstOrDefault(wanted);
        }

        public void Take(string item, DateTimeOffset now)
        {
            patch.Advance(now);
            if (patch.Take(1, now, item) == 1)
            {
                patch.Advance(now);
            }
        }

        public bool Accepts(string item, DateTimeOffset now) => false;

        public void Put(string item, DateTimeOffset now)
        {
        }

        public DateTimeOffset? NextChange(DateTimeOffset now)
        {
            patch.Advance(now);
            return patch.Full ? null : patch.NextCut();
        }

        public Task SaveAsync(CancellationToken cancellationToken) =>
            service.SaveAsync(x, y, forests.Where(f => f.Patch == patch).ToList(), cancellationToken);
    }
}

public sealed record ForestryRequest(int X, int Y);

public sealed record TakeFromForesterRequest(int X, int Y, Guid Id);

public sealed record PlantForesterRequest(int X, int Y, Guid Id, string Species);

public abstract class ForestryHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var (state, taken, xp) = await RunAsync(
            scope.ServiceProvider.GetRequiredService<ForesterService>(), scope.ServiceProvider.GetServices<IPlotAutomation>(),
            client.RequireAccount(), payload, cancellationToken);
        var biome = scope.ServiceProvider.GetRequiredService<WorldMap>().BiomeAt(state.X, state.Y);
        return new Reply("forestry", new
        {
            x = state.X,
            y = state.Y,
            foresters = state.Foresters.Select(f => ForesterJson(f, biome, time.GetUtcNow())),
            trees = state.Trees.Select(PlantTreeHandler.TreeJson),
            cleared = state.Cleared.Select(t => new { tileX = t.X, tileY = t.Y }),
            taken = taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = xp is { } gain ? new { xp = gain.Xp, level = gain.Level, leveledUp = gain.LeveledUp } : null,
            serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<(PlotForests, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        ForesterService foresters, IEnumerable<IPlotAutomation> automation, Guid player, JsonElement? payload, CancellationToken cancellationToken);

    public static object ForesterJson(ForesterOnPlot forester, Biome biome, DateTimeOffset now)
    {
        var (o, patch) = (forester.Object, forester.Patch);
        var level = patch.ForesterLevel;
        var next = Foresters.Levels.FirstOrDefault(l => l.Level == level.Level + 1);
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
            clearing = Foresters.Clearing,
            wood = patch.Wood,
            store = patch.Store.OrderBy(s => s.Key == Items.Wood ? 0 : 1).Select(s => new { id = s.Key, name = Items.Names[s.Key], quantity = s.Value }),
            capacity = level.Capacity,
            cutSeconds = level.CutTime.TotalSeconds,
            growSeconds = Foresters.GrowTime(level, biome, patch.Species)?.TotalSeconds,
            // A tree nursery near it raises its saplings: its forest grows faster.
            nursed = level.GrowSpeed > (Foresters.Level(o.Kind)?.GrowSpeed ?? level.GrowSpeed),
            species = patch.Species.Id,
            // The kinds it can plant, how long each grows in its care here and what it gives.
            kinds = Trees.Kinds.Select(k => new
            {
                id = k.Id,
                name = Trees.Names[k.Id],
                sapling = k.Sapling,
                growSeconds = Foresters.GrowTime(level, biome, k)?.TotalSeconds,
                wood = k.Wood,
                woodName = Items.Names[k.Wood],
                woodQuantity = k.WoodQuantity,
                flowers = k.Flowers,
                note = Trees.Notes[k.Id],
                standing = patch.Trees.Keys.Count(t => patch.KindAt(t) == k) + patch.Wild.Values.Count(v => v == k),
            }),
            trees = patch.Trees.Count + patch.Wild.Count,
            grown = patch.Wild.Count + patch.Trees.Values.Count(r => r <= now),
            full = patch.Full,
            // The next level: a building made at the workbench from this one.
            upgrade = next is null ? null : new
            {
                kind = next.Kind,
                name = Items.Names[next.Kind],
                radius = next.Radius,
                capacity = next.Capacity,
                cutSeconds = next.CutTime.TotalSeconds,
                growSeconds = Foresters.GrowTime(next, biome, patch.Species)?.TotalSeconds,
            },
        };
    }
}

/// <summary>The plot's foresters, their forests and every planted tree, worked up to now; the client asks every few seconds.</summary>
public sealed class GetForestryHandler(IServiceScopeFactory scopes, TimeProvider time) : ForestryHandler(scopes, time)
{
    public override string Type => "forestry.get";

    protected override async Task<(PlotForests, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        ForesterService foresters, IEnumerable<IPlotAutomation> automation, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<ForestryRequest>(payload);
        return (await foresters.StateAsync(player, request.X, request.Y, automation, cancellationToken), [], null);
    }
}

/// <summary>Takes all the wood out of a forester into the inventory.</summary>
public sealed class TakeFromForesterHandler(IServiceScopeFactory scopes, TimeProvider time) : ForestryHandler(scopes, time)
{
    public override string Type => "forester.take";

    protected override Task<(PlotForests, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        ForesterService foresters, IEnumerable<IPlotAutomation> automation, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TakeFromForesterRequest>(payload);
        return foresters.TakeAsync(player, request.X, request.Y, request.Id, automation, cancellationToken);
    }
}

/// <summary>Picks the kind of tree a forester plants from now on.</summary>
public sealed class PlantForesterHandler(IServiceScopeFactory scopes, TimeProvider time) : ForestryHandler(scopes, time)
{
    public override string Type => "forester.plant";

    protected override async Task<(PlotForests, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        ForesterService foresters, IEnumerable<IPlotAutomation> automation, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlantForesterRequest>(payload);
        return (await foresters.PlantAsync(player, request.X, request.Y, request.Id, request.Species, automation, cancellationToken), [], null);
    }
}
