using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>What stands on one tile inside a plot. Sent to the client, so never renumber.</summary>
public enum TileObject : byte
{
    None = 0,
    Tree = 1,
    Rock = 2,
    Dune = 3,
    Rail = 4,
    Station = 5,
    Bush = 6,
    Mine = 7,
    Water = 8,
    Flotsam = 9,

    /// <summary>A block of the glacier's ice, cut with a pickaxe; it freezes again after a blizzard.</summary>
    IceBlock = 10,

    /// <summary>A dark stone fallen from the sky onto the ice, broken with a pickaxe for meteoric iron and now and then iridium.</summary>
    Meteorite = 11,

    /// <summary>A crack in the glacier: nothing stands on it until a bridge of beams is laid over it.</summary>
    Crevasse = 12,

    /// <summary>A crevasse a bridge was laid over: anything may stand on it.</summary>
    Bridge = 13,

    /// <summary>Snow an avalanche left in a blizzard, dug away with a shovel; whatever stands under it is buried until then.</summary>
    Snowdrift = 14,

    /// <summary>Trees of other kinds than the pine (<see cref="Tree"/>), see <see cref="Trees"/>.</summary>
    SpruceTree = 15,
    BirchTree = 16,
    OakTree = 17,
    LindenTree = 18,

    /// <summary>Mushrooms on the floor of an old forest, picked by hand; they come up again after <see cref="Forestry.MushroomRegrow"/>.</summary>
    Mushrooms = 19,
}

/// <summary>
/// The inside of a plot: Size × Size tiles of 20 m. Every plot is generated the same way from its
/// position, so only what players changed (harvested trees, rocks...) is stored. Along the top
/// edge runs the railway with the plot's station; the rest is covered by the biome's nature.
/// </summary>
public static class PlotLayout
{
    public const int Size = 250;

    public const int TileMeters = PlotRules.SideKm * 1000 / Size;

    /// <summary>The track runs across the whole plot on this row.</summary>
    public const int RailRow = 9;

    /// <summary>The station building, standing on the track's north side.</summary>
    public static readonly (int X, int Y, int Width, int Height) Station = (119, 3, 12, 6);

    /// <summary>Where the player appears, in front of the station, on the cleared strip south of the track.</summary>
    public static readonly (int X, int Y) Spawn = (125, 11);

    /// <summary>Rows kept clear around the railway.</summary>
    public const int ClearRows = 12;

    /// <summary>Chance (per mille) of each object on a tile, by biome; scaled by a patchy noise.</summary>
    private static readonly IReadOnlyDictionary<Biome, (TileObject Object, int PerMille)[]> Nature = new Dictionary<Biome, (TileObject, int)[]>
    {
        [Biome.Forest] = [(TileObject.Tree, 380), (TileObject.Rock, 8), (TileObject.Bush, 30)],
        [Biome.Farmland] = [(TileObject.Tree, 25), (TileObject.Bush, 25), (TileObject.Rock, 4)],
        [Biome.Grassland] = [(TileObject.Tree, 15), (TileObject.Bush, 40), (TileObject.Rock, 10)],
        [Biome.Desert] = [(TileObject.Dune, 90), (TileObject.Rock, 18), (TileObject.Bush, 6)],
        // No trees grow in the mountains; the gap they left keeps every rock and bush where it stood.
        [Biome.Mountains] = [(TileObject.Rock, 260), (TileObject.None, 45), (TileObject.Bush, 10)],
        [Biome.Tundra] = [(TileObject.Rock, 40), (TileObject.Bush, 35), (TileObject.Tree, 8)],
        [Biome.Ice] = [(TileObject.Rock, 25), (TileObject.IceBlock, 70), (TileObject.Crevasse, 8), (TileObject.Meteorite, 2)],
        [Biome.City] = [],
        [Biome.Coast] = [(TileObject.Tree, 50), (TileObject.Dune, 30), (TileObject.Rock, 15)],
        [Biome.Lakeshore] = [(TileObject.Tree, 90), (TileObject.Bush, 40), (TileObject.Rock, 10)],
        // Date palms around the pond of an oasis, on sand.
        [Biome.Oasis] = [(TileObject.Dune, 60), (TileObject.Tree, 45), (TileObject.Bush, 25), (TileObject.Rock, 8)],
    };

    public static bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size;

    /// <summary>One byte per tile (row by row): the <see cref="TileObject"/> standing there before anyone touched it.</summary>
    public static byte[] Generate(int plotX, int plotY, Biome biome)
    {
        var tiles = new byte[Size * Size];
        var nature = Nature.GetValueOrDefault(biome, []);
        var seed = (uint)(plotX * 73856093) ^ (uint)(plotY * 19349663);
        for (var y = ClearRows; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                // Objects grow in patches with clearings between them.
                var patch = 0.3 + 1.4 * Noise(seed, x / 14.0, y / 14.0);
                var roll = Hash(seed, x, y) % 1000;
                var at = 0.0;
                foreach (var (obj, perMille) in nature)
                {
                    at += perMille * patch;
                    if (roll < at)
                    {
                        tiles[y * Size + x] = (byte)obj;
                        break;
                    }
                }
            }
        }

        Coast.AddWater(tiles, seed, biome);
        Trees.Assign(tiles, seed, biome);
        Forestry.AddMushrooms(tiles, seed, biome);
        for (var x = 0; x < Size; x++)
        {
            tiles[RailRow * Size + x] = (byte)TileObject.Rail;
        }

        // Each mine stands on cleared ground with an open strip in front of its entrance.
        foreach (var mine in Mines(plotX, plotY, biome))
        {
            for (var y = mine.Y - 1; y < mine.Y + MineSite.Height + 3; y++)
            {
                for (var x = mine.X - 1; x < mine.X + MineSite.Width + 1; x++)
                {
                    var inside = x >= mine.X && x < mine.X + MineSite.Width && y >= mine.Y && y < mine.Y + MineSite.Height;
                    tiles[y * Size + x] = (byte)(inside ? TileObject.Mine : TileObject.None);
                }
            }
        }

        var (sx, sy, sw, sh) = Station;
        for (var y = sy; y < sy + sh; y++)
        {
            for (var x = sx; x < sx + sw; x++)
            {
                tiles[y * Size + x] = (byte)TileObject.Station;
            }
        }

        return tiles;
    }

    /// <summary>
    /// The mines of a plot: 1-5 on a mining plot, none elsewhere. The first stands near the
    /// station, the others anywhere, never touching each other. Always the same for a plot.
    /// </summary>
    public static IReadOnlyList<MineSite> Mines(int plotX, int plotY, Biome biome)
    {
        if (!Mining.HasMines(biome))
        {
            return [];
        }

        var seed = (uint)(plotX * 83492791) ^ (uint)(plotY * 2654435761);
        var count = 1 + (int)(Hash(seed, 0, 0) % 5);
        var mines = new List<MineSite>();
        for (var attempt = 1; mines.Count < count && attempt < 500; attempt++)
        {
            var (x, y) = mines.Count == 0
                ? (Spawn.X - 20 + (int)(Hash(seed, attempt, 1) % 36), ClearRows + 6 + (int)(Hash(seed, attempt, 2) % 14))
                : (4 + (int)(Hash(seed, attempt, 1) % (Size - 8 - MineSite.Width)), ClearRows + 4 + (int)(Hash(seed, attempt, 2) % (Size - ClearRows - 12)));
            if (mines.All(m => Math.Abs(m.X - x) > MineSite.Width + 8 || Math.Abs(m.Y - y) > MineSite.Height + 8))
            {
                mines.Add(new MineSite(mines.Count, x, y));
            }
        }

        return mines;
    }

    /// <summary>Whether an object can be gathered: a bush and what the sea threw up are picked up by hand, the rest needs <see cref="ToolFor"/>.</summary>
    public static bool Gatherable(TileObject obj) => obj is TileObject.Bush or TileObject.Flotsam or TileObject.Mushrooms || ToolFor(obj) is not null;

    /// <summary>Beams a bridge over a crevasse takes.</summary>
    public const int BridgeBeams = 3;

    /// <summary>Which tool a player needs to gather an object, or null when none does (or it cannot be gathered).</summary>
    public static string? ToolFor(TileObject obj) => obj switch
    {
        _ when Trees.IsTree(obj) => Items.Axe,
        TileObject.Rock or TileObject.IceBlock or TileObject.Meteorite => Items.Pickaxe,
        TileObject.Dune or TileObject.Snowdrift => Items.Shovel,
        TileObject.Water => Items.FishingRod,
        _ => null,
    };

    /// <summary>The tool a player gets with a newly bought plot of a biome, or null.</summary>
    public static string? StarterTool(Biome biome) => biome switch
    {
        Biome.Forest => Items.Axe,
        Biome.Desert or Biome.Oasis => Items.Shovel,
        Biome.Mountains => Items.Pickaxe,
        Biome.Farmland => Items.Hoe,
        Biome.Grassland => Items.Shears,
        Biome.Coast or Biome.Lakeshore => Items.FishingRod,
        Biome.Ice => Items.Pickaxe,
        _ => null,
    };

    /// <summary>Of dunes dug, this many in a hundred give clay too.</summary>
    public const int ClayChance = 15;

    /// <summary>Of dunes dug, this many in a thousand give up a fossil: an ammonite, an ancient coin or a dinosaur bone.</summary>
    public const int FossilPerMille = 4;

    /// <summary>Of meteorites broken, this many in a hundred give iridium too.</summary>
    public const int IridiumChance = 5;

    /// <summary>A reed fence keeps the sand of a sandstorm off dug dunes this many tiles from it each way.</summary>
    public const int FenceShelter = 4;

    /// <summary>What gathering an object on a plot of <paramref name="biome"/> gives, for a roll of 0-99 and a <paramref name="rare"/> one of 0-999:
    /// a palm in an oasis gives dates, a dune on sand 30% more sand, and now and then clay or a fossil. A tree cut with a better
    /// <paramref name="saw"/> gives more.</summary>
    public static IReadOnlyList<(string Item, int Quantity)> Yield(TileObject obj, int roll, Biome biome = Biome.Ocean, int rare = 999, Saw? saw = null) => obj switch
    {
        TileObject.Tree when biome == Biome.Oasis => [(Items.Wood, 1 + (saw?.ExtraWood ?? 0)), (Items.Dates, 2)],
        _ when Trees.ByTile.TryGetValue(obj, out var kind) => Trees.Yield(kind, roll, rare, saw: saw),
        TileObject.Mushrooms => roll < Forestry.HerbChance ? [(Items.Mushrooms, 2), (Items.Herbs, 1)] : [(Items.Mushrooms, 2)],
        TileObject.Bush => Forestry.BushYield(biome, roll),
        TileObject.Dune => [(Items.Sand, Oasis.Sandy((int)biome) ? 4 : 3), .. Dug(roll, rare)],
        TileObject.IceBlock => [(Items.Ice, 2)],
        TileObject.Snowdrift => [(Items.Ice, 1)],
        TileObject.Meteorite => roll < IridiumChance ? [(Items.MeteoricIron, 1), (Items.Iridium, 1)] : [(Items.MeteoricIron, 1)],
        TileObject.Rock => roll switch
        {
            < 5 => [(Items.Stone, 2), (Items.CopperOre, 1)],
            < 15 => [(Items.Stone, 2), (Items.IronOre, 1)],
            < 30 => [(Items.Stone, 2), (Items.Coal, 1)],
            _ => [(Items.Stone, 2)],
        },
        _ => [],
    };

    private static IEnumerable<(string, int)> Dug(int roll, int rare)
    {
        if (roll < ClayChance)
        {
            yield return (Items.Clay, 1);
        }

        if (rare < FossilPerMille)
        {
            yield return (rare < 2 ? Items.Ammonite : rare < 3 ? Items.AncientCoin : Items.DinosaurBone, 1);
        }
    }

    internal static uint Hash(uint seed, int x, int y)
    {
        var h = seed ^ (uint)x * 0x27d4eb2du ^ (uint)y * 0x165667b1u;
        h ^= h >> 15;
        h *= 0x85ebca6bu;
        h ^= h >> 13;
        h *= 0xc2b2ae35u;
        return h ^ (h >> 16);
    }

    /// <summary>Smooth value noise in 0..1.</summary>
    internal static double Noise(uint seed, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double Corner(int cx, int cy) => (Hash(seed + 7919, cx, cy) & 0xffff) / 65535.0;
        double Smooth(double t) => t * t * (3 - 2 * t);
        var top = Corner(x0, y0) + (Corner(x0 + 1, y0) - Corner(x0, y0)) * Smooth(fx);
        var bottom = Corner(x0, y0 + 1) + (Corner(x0 + 1, y0 + 1) - Corner(x0, y0 + 1)) * Smooth(fx);
        return top + (bottom - top) * Smooth(fy);
    }
}

public static class HarvestRules
{
    /// <summary>How long one swing of a tool takes; the server refuses faster gathering.</summary>
    public static readonly TimeSpan Time = TimeSpan.FromSeconds(1.5);

    /// <summary>Cutting down a tree takes longer than any other work.</summary>
    public static readonly TimeSpan ChopTime = TimeSpan.FromSeconds(5);

    /// <summary>A chainsaw cuts a tree 70% faster than an axe.</summary>
    public static readonly TimeSpan SawTime = ChopTime * Items.Saws[Items.Chainsaw].Time;

    /// <summary>How long gathering an object takes with the tool in hand on a plot of <paramref name="biome"/>: a rock
    /// breaks faster in the mountains and with a better pickaxe.</summary>
    public static TimeSpan TimeFor(TileObject obj, string? tool = null, Biome biome = Biome.Ocean) => obj switch
    {
        _ when Trees.IsTree(obj) => ChopTime * (Items.Saws.TryGetValue(tool ?? "", out var saw) ? saw.Time : 1),
        TileObject.Water => Coast.FishTime(biome),
        TileObject.Rock or TileObject.IceBlock => Time / (Mining.Speed(biome) * Items.Pickaxes.GetValueOrDefault(tool ?? "", 1)),
        // A meteorite is twice as hard as a rock.
        TileObject.Meteorite => 2 * Time / (Mining.Speed(biome) * Items.Pickaxes.GetValueOrDefault(tool ?? "", 1)),
        _ => Time,
    };

    public const int XpPerHarvest = 2;

    /// <summary>The industry whose clothes make gathering an object faster, or null.</summary>
    public static string? Industry(TileObject obj) => obj switch
    {
        _ when Trees.IsTree(obj) => Industries.Forestry,
        TileObject.Bush or TileObject.Mushrooms => Industries.Forestry,
        TileObject.Rock or TileObject.IceBlock or TileObject.Meteorite or TileObject.Snowdrift => Industries.Mining,
        TileObject.Water => Industries.Fishing,
        _ => null,
    };
}

/// <param name="Felled">The trees next to the one cut that a saw felled at the same cut.</param>
public sealed record Harvest(TileObject Object, IReadOnlyList<(string Item, int Quantity)> Items, XpGain Xp, IReadOnlyList<(int X, int Y)>? Felled = null);

public sealed class PlotService(
    GameDb db, WorldMap map, IInventory inventory, IEquipment equipment, ICharacterProgress progress, IProductionStats stats,
    IEnumerable<ICompanyPlots> companies, BeachService beach, PestService pests, ForestLedger ledger, IOutfit outfit, PlotTaxService taxes,
    TimeProvider time, IServiceProvider services, PlotPermits permits)
{
    /// <summary>The tiles of a plot as they are now; only its owner and members of its company may enter, or anyone when it is public.</summary>
    public async Task<byte[]> TilesAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        var biome = await RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Visit);
        return await TilesAsync(x, y, biome, cancellationToken);
    }

    /// <summary>The tiles of a plot of <paramref name="biome"/> as they are now, whoever asks.</summary>
    public async Task<byte[]> TilesAsync(int x, int y, Biome biome, CancellationToken cancellationToken)
    {
        var tiles = PlotLayout.Generate(x, y, biome);
        // On sand the last sandstorm blew the dunes dug before it back, where nothing stands and no reed fence is near; on the
        // glacier the ice cut before the last blizzard froze again, and its avalanches left snow about.
        var blizzard = biome == Biome.Ice ? Blizzard.LastEnded(time.GetUtcNow()) : null;
        var storm = Oasis.Sandy((int)biome) ? SandWeather.LastEnded(time.GetUtcNow()) : blizzard?.End;
        var dug = new HashSet<int>();
        var now = time.GetUtcNow();
        var picked = now - Forestry.MushroomRegrow;
        var harvests = new List<(int TileX, int TileY, DateTimeOffset At)>();
        // What stands on a tile keeps what was gathered there from coming back; a reed fence shelters the tiles around it.
        // Each table is read once and matched here, as a plot may have hundreds of objects and of tiles gathered.
        var standing = new HashSet<(int, int)>();
        var fences = new List<(int X, int Y)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT 0, square, 0, harvested_at, NULL FROM world.plot_harvests WHERE x = @x AND y = @y
            UNION ALL SELECT 1, tile_x, tile_y, NULL, kind FROM world.plot_objects WHERE x = @x AND y = @y
            UNION ALL SELECT 2, tile_x, tile_y, NULL, NULL FROM world.plot_fields WHERE x = @x AND y = @y
            UNION ALL SELECT 2, tile_x, tile_y, NULL, NULL FROM world.plot_trees WHERE x = @x AND y = @y
            UNION ALL SELECT 2, tile_x, tile_y, NULL, NULL FROM world.plot_animals WHERE x = @x AND y = @y
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var (tx, ty) = (reader.GetInt32(1), reader.GetInt32(2));
                switch (reader.GetInt32(0))
                {
                    case 0:
                        harvests.AddRange(PlotHarvests.Gathered(tx, reader.GetFieldValue<DateTimeOffset?[]>(3)));
                        break;
                    case 1:
                        var kind = reader.GetString(4);
                        standing.UnionWith(Footprints.Tiles(kind, tx, ty));
                        if (kind == Items.ReedFence)
                        {
                            fences.Add((tx, ty));
                        }

                        break;
                    default:
                        standing.Add((tx, ty));
                        break;
                }
            }
        }

        foreach (var (tx, ty) in fences)
        {
            for (var dy = -PlotLayout.FenceShelter; dy <= PlotLayout.FenceShelter; dy++)
            {
                for (var dx = -PlotLayout.FenceShelter; dx <= PlotLayout.FenceShelter; dx++)
                {
                    standing.Add((tx + dx, ty + dy));
                }
            }
        }

        foreach (var (tileX, tileY, harvestedAt) in harvests)
        {
            // A rock broken before the mines came may have stood where a mine is now, and the
            // lake or sea came over what had been gathered on the shore.
            var at = tileY * PlotLayout.Size + tileX;
            // Sand and ice come back with the storm; mushrooms come up again by themselves.
            // Gathered before the storm or long enough ago, with nothing standing there since.
            var beforeStorm = harvestedAt < storm;
            var due = (beforeStorm || harvestedAt < picked) && !standing.Contains((tileX, tileY));
            var regrown = due && ((beforeStorm && tiles[at] is (byte)TileObject.Dune or (byte)TileObject.IceBlock)
                || tiles[at] == (byte)TileObject.Mushrooms);
            if (harvestedAt >= storm)
            {
                dug.Add(at);
            }

            if (tiles[at] == (byte)TileObject.Crevasse)
            {
                tiles[at] = (byte)TileObject.Bridge;
            }
            else if (tiles[at] is not ((byte)TileObject.Mine or (byte)TileObject.Water) && !regrown)
            {
                tiles[at] = (byte)TileObject.None;
            }
        }

        // Snow the avalanches of the last blizzard left on open ice and on whatever stands there, where nobody dug it away since.
        if (blizzard is { } last)
        {
            for (var ty = PlotLayout.ClearRows; ty < PlotLayout.Size; ty++)
            {
                for (var tx = 0; tx < PlotLayout.Size; tx++)
                {
                    var at = ty * PlotLayout.Size + tx;
                    if (tiles[at] == (byte)TileObject.None && !dug.Contains(at) && Blizzard.Drift(last.Window, x, y, tx, ty))
                    {
                        tiles[at] = (byte)TileObject.Snowdrift;
                    }
                }
            }
        }

        // What the sea threw up today on the beach, where nothing stands.
        foreach (var (tx, ty) in await beach.FindsAsync(x, y, biome, tiles, time.GetUtcNow(), cancellationToken))
        {
            tiles[ty * PlotLayout.Size + tx] = (byte)TileObject.Flotsam;
        }

        return tiles;
    }

    /// <summary>Whether nothing stands on a tile of the plot: no nature, field, planted tree, animal or placed object
    /// (a forester's building covers the tiles around its own too).</summary>
    public async Task<bool> FreeAsync(Guid player, int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        if (!PlotLayout.Inside(tileX, tileY)
            || (await TilesAsync(player, x, y, cancellationToken))[tileY * PlotLayout.Size + tileX] is not ((byte)TileObject.None or (byte)TileObject.Bridge))
        {
            return false;
        }

        await using var command = await db.CommandAsync(
            $$"""
            SELECT EXISTS (SELECT 1 FROM world.plot_fields WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty)
                OR EXISTS (SELECT 1 FROM world.plot_trees WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty)
                OR EXISTS (SELECT 1 FROM world.plot_animals WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty)
                OR EXISTS (SELECT 1 FROM world.plot_objects o WHERE o.x = @x AND o.y = @y AND {{Footprints.CoversSql("o", "@tx", "@ty")}})
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("tx", tileX);
        command.Parameters.AddWithValue("ty", tileY);
        // Objects on more than one tile.
        command.Parameters.AddWithValue("sizedKinds", Footprints.SizedKinds);
        command.Parameters.AddWithValue("sizedSizes", Footprints.SizedSizes);
        return !(bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Cuts a tree (grown by itself or planted), breaks a rock or digs a dune with the tool in the
    /// player's slot, or pulls out a bush by hand. <paramref name="pace"/> gets how long the work takes and refuses it if too quick.
    /// </summary>
    public Task<Harvest> HarvestAsync(
        Guid player, int x, int y, int tileX, int tileY, Action<TimeSpan> pace, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var biome = await RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            if (!PlotLayout.Inside(tileX, tileY))
            {
                throw new GameException("nothing_here", "There is nothing to gather here.");
            }

            if (await PlantedAtAsync(x, y, tileX, tileY, cancellationToken) is { } planted)
            {
                return await CutPlantedAsync(player, x, y, tileX, tileY, planted, pace, cancellationToken);
            }

            var obj = (TileObject)PlotLayout.Generate(x, y, biome)[tileY * PlotLayout.Size + tileX];
            var current = (TileObject)(await TilesAsync(x, y, biome, cancellationToken))[tileY * PlotLayout.Size + tileX];
            if (current == TileObject.Flotsam)
            {
                pace(HarvestRules.Time);
                return await beach.GatherAsync(player, x, y, tileX, tileY, async items => await GiveAsync(player, TileObject.Flotsam, items, cancellationToken), cancellationToken);
            }

            if (current == TileObject.Snowdrift)
            {
                // Snow dug away, maybe off something it buried; it comes back only with the next blizzard's avalanches.
                pace(HarvestRules.TimeFor(current, await RequireToolAsync(player, Items.Shovel, cancellationToken), biome)
                     / await SpeedAsync(player, Industries.Mining, cancellationToken, (x, y, tileX, tileY)));
                await MarkHarvestedAsync(x, y, tileX, tileY, always: true, cancellationToken);
                return await GiveAsync(player, current, PlotLayout.Yield(current, Random.Shared.Next(100), biome), cancellationToken);
            }

            if (current == TileObject.Crevasse)
            {
                pace(HarvestRules.Time);
                await inventory.RemoveAsync(player, Items.Beams, PlotLayout.BridgeBeams, cancellationToken);
                await MarkHarvestedAsync(x, y, tileX, tileY, always: false, cancellationToken);
                return new Harvest(TileObject.Bridge, [], await progress.AddXpAsync(player, HarvestRules.XpPerHarvest, cancellationToken));
            }

            if (current == TileObject.Bridge)
            {
                throw new GameException("nothing_here", "A bridge lies over the crevasse already.");
            }

            if (!PlotLayout.Gatherable(obj))
            {
                throw new GameException("nothing_here", "There is nothing to gather here.");
            }

            var held = PlotLayout.ToolFor(obj) is { } tool ? await RequireToolAsync(player, tool, cancellationToken) : null;
            pace(HarvestRules.TimeFor(obj, held, biome) / await SpeedAsync(player, HarvestRules.Industry(obj), cancellationToken, (x, y, tileX, tileY)));
            if (obj == TileObject.Water)
            {
                // The water is never fished out; a storm at sea or on a lake keeps everyone ashore but those on a breakwater.
                if (Coast.HasWater(biome))
                {
                    await beach.RequireCalmAsync(x, y, cancellationToken);
                }

                return await GiveAsync(player, obj, Coast.Catch(biome, Random.Shared.Next(100)), cancellationToken);
            }

            // A dune the sandstorm blew back is dug again, ice frozen again after a blizzard is cut again, and mushrooms come up again.
            await MarkHarvestedAsync(x, y, tileX, tileY, obj is TileObject.Dune or TileObject.IceBlock or TileObject.Mushrooms && current == obj, cancellationToken);
            if (Trees.IsTree(obj))
            {
                var saw = Items.Saws.GetValueOrDefault(held!);
                var cut = PlotLayout.Yield(obj, Random.Shared.Next(100), biome, Random.Shared.Next(1000), saw);
                return await GiveTreesAsync(player, x, y, obj, cut, await FellAroundAsync(x, y, biome, tileX, tileY, saw, cancellationToken), cancellationToken);
            }

            return await GiveAsync(player, obj, PlotLayout.Yield(obj, Random.Shared.Next(100), biome, Random.Shared.Next(1000)), cancellationToken);
        }, cancellationToken);

    /// <summary>Records that a tile not gathered before was gathered now; false when it was.</summary>
    private Task<bool> TryMarkHarvestedAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        PlotHarvests.MarkAsync(db, x, y, tileX, tileY, time.GetUtcNow(), again: false, cancellationToken);

    /// <summary>Records that a tile was gathered now; <paramref name="always"/> also when it was before. Fails when it was and may not be again.</summary>
    private async Task MarkHarvestedAsync(int x, int y, int tileX, int tileY, bool always, CancellationToken cancellationToken)
    {
        if (!await PlotHarvests.MarkAsync(db, x, y, tileX, tileY, time.GetUtcNow(), always, cancellationToken))
        {
            throw new GameException("nothing_here", "This has already been gathered.");
        }
    }

    /// <summary>Whether snow an avalanche left on the glacier buries the tile an object stands on: it cannot be used until dug out.</summary>
    public async Task<bool> BuriedAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        map.BiomeAt(x, y) == Biome.Ice && PlotLayout.Inside(tileX, tileY)
        && (await TilesAsync(x, y, Biome.Ice, cancellationToken))[tileY * PlotLayout.Size + tileX] == (byte)TileObject.Snowdrift;

    /// <summary>Fails with <c>buried</c> when snow buries the object.</summary>
    public async Task RequireDugOutAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        if (await BuriedAsync(x, y, tileX, tileY, cancellationToken))
        {
            throw new GameException("buried", "Snow buries it: dig it out with a shovel first.");
        }
    }

    /// <summary>The tree planted on a tile: when it is grown, its kind and whether a player planted it by hand (locked until the
    /// transaction ends), or null when none is.</summary>
    private async Task<(DateTimeOffset ReadyAt, TreeKind Kind, bool ByHand)?> PlantedAtAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(
            "SELECT ready_at, species, forester_id IS NULL FROM world.plot_trees WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty FOR UPDATE",
            cancellationToken);
        select.Parameters.AddWithValue("x", x);
        select.Parameters.AddWithValue("y", y);
        select.Parameters.AddWithValue("tx", tileX);
        select.Parameters.AddWithValue("ty", tileY);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetFieldValue<DateTimeOffset>(0), Trees.Of(reader.GetString(1)), reader.GetBoolean(2))
            : null;
    }

    private async Task<Harvest> CutPlantedAsync(
        Guid player, int x, int y, int tileX, int tileY, (DateTimeOffset ReadyAt, TreeKind Kind, bool ByHand) tree, Action<TimeSpan> pace,
        CancellationToken cancellationToken)
    {
        if (tree.ReadyAt > time.GetUtcNow())
        {
            throw new GameException("not_grown", "The tree has not grown yet.");
        }

        // The bark beetle may be in a tree of a monoculture planted by hand.
        var tool = await RequireToolAsync(player, Items.Axe, cancellationToken);
        pace(HarvestRules.TimeFor(tree.Kind.Tile, tool) / await SpeedAsync(player, Industries.Forestry, cancellationToken, (x, y, tileX, tileY)));
        var saw = Items.Saws.GetValueOrDefault(tool);
        var cut = await CutDownAsync(x, y, tileX, tileY, tree, saw, cancellationToken);
        return await GiveTreesAsync(player, x, y, tree.Kind.Tile, cut, await FellAroundAsync(x, y, map.BiomeAt(x, y), tileX, tileY, saw, cancellationToken), cancellationToken);
    }

    /// <summary>Takes a grown planted tree off its tile and says what it gives cut with <paramref name="saw"/>.</summary>
    private async Task<IReadOnlyList<(string Item, int Quantity)>> CutDownAsync(
        int x, int y, int tileX, int tileY, (DateTimeOffset ReadyAt, TreeKind Kind, bool ByHand) tree, Saw? saw, CancellationToken cancellationToken)
    {
        // The bark beetle may be in a tree of a monoculture planted by hand.
        var infested = tree.ByHand && (await pests.StateAsync(x, y, cancellationToken)).Infested.Contains((tileX, tileY));
        await using (var delete = await db.CommandAsync(
            "DELETE FROM world.plot_trees WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty", cancellationToken))
        {
            delete.Parameters.AddWithValue("x", x);
            delete.Parameters.AddWithValue("y", y);
            delete.Parameters.AddWithValue("tx", tileX);
            delete.Parameters.AddWithValue("ty", tileY);
            // Cut by someone else in the meantime: one tree never gives wood twice.
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("nothing_here", "This has already been gathered.");
            }
        }

        return Trees.Yield(tree.Kind, Random.Shared.Next(100), Random.Shared.Next(1000), infested, saw);
    }

    /// <summary>The tiles next to a tile, the four at its sides before the four at its corners.</summary>
    private static readonly (int X, int Y)[] Around = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1)];

    /// <summary>Fells as many grown trees next to the one cut on (<paramref name="tileX"/>, <paramref name="tileY"/>) as
    /// <paramref name="saw"/> takes down at a cut, planted or wild: the tiles they stood on and what each gave.</summary>
    private async Task<IReadOnlyList<((int X, int Y) Tile, IReadOnlyList<(string Item, int Quantity)> Items)>> FellAroundAsync(
        int x, int y, Biome biome, int tileX, int tileY, Saw? saw, CancellationToken cancellationToken)
    {
        var felled = new List<((int, int), IReadOnlyList<(string, int)>)>();
        if (saw is not { Fells: > 0 })
        {
            return felled;
        }

        var tiles = await TilesAsync(x, y, biome, cancellationToken);
        foreach (var (dx, dy) in Around)
        {
            if (felled.Count == saw.Fells)
            {
                break;
            }

            var (tx, ty) = (tileX + dx, tileY + dy);
            if (!PlotLayout.Inside(tx, ty))
            {
                continue;
            }

            if (await PlantedAtAsync(x, y, tx, ty, cancellationToken) is { } planted)
            {
                if (planted.ReadyAt <= time.GetUtcNow())
                {
                    felled.Add(((tx, ty), await CutDownAsync(x, y, tx, ty, planted, saw, cancellationToken)));
                }

                continue;
            }

            var obj = (TileObject)tiles[ty * PlotLayout.Size + tx];
            if (Trees.IsTree(obj) && await TryMarkHarvestedAsync(x, y, tx, ty, cancellationToken))
            {
                felled.Add(((tx, ty), PlotLayout.Yield(obj, Random.Shared.Next(100), biome, Random.Shared.Next(1000), saw)));
            }
        }

        return felled;
    }

    /// <summary>Gives what a cut tree and those felled with it gave, and counts them all as cut for the forest certificate.</summary>
    private async Task<Harvest> GiveTreesAsync(
        Guid player, int x, int y, TileObject obj, IReadOnlyList<(string Item, int Quantity)> cut,
        IReadOnlyList<((int X, int Y) Tile, IReadOnlyList<(string Item, int Quantity)> Items)> felled, CancellationToken cancellationToken)
    {
        await ledger.RecordAsync(player, 0, 1 + felled.Count, cancellationToken);
        // Where the forest is protected, cutting without the certificate is against the law (resolved here: the law asks about plots).
        foreach (var watch in services.GetServices<ILawWatch>())
        {
            await watch.TreesCutAsync(player, x, y, 1 + felled.Count, cancellationToken);
        }

        var items = cut.Concat(felled.SelectMany(f => f.Items)).GroupBy(i => i.Item).Select(g => (g.Key, g.Sum(i => i.Quantity))).ToList();
        var harvest = await GiveAsync(player, obj, items, cancellationToken, 1 + felled.Count);
        return harvest with { Felled = felled.Select(f => f.Tile).ToList() };
    }

    /// <summary>How many times faster the player's clothes and last meal make work in an industry (1 for none); for forestry and mining
    /// on a plot (<paramref name="at"/>, with the tile when the work has one), faster still with a fed work horse at hand.</summary>
    public async Task<double> SpeedAsync(Guid player, string? industry, CancellationToken cancellationToken, (int X, int Y, int? TileX, int? TileY)? at = null)
    {
        if (industry is null)
        {
            return 1;
        }

        var speed = Industries.Speed(await outfit.BonusAsync(player, industry, cancellationToken));
        if (industry is Industries.Forestry or Industries.Mining && at is { } plot
            && await Husbandry.HorseAtWorkAsync(db, plot.X, plot.Y, plot.TileX is { } tx && plot.TileY is { } ty ? (tx, ty) : null, time.GetUtcNow(), cancellationToken))
        {
            speed *= Husbandry.HorseBonus;
        }

        return speed;
    }

    /// <summary>The tool in the player's tool bar that does the work of <paramref name="tool"/>: a saw does an axe's,
    /// and a bronze or steel pickaxe an iron one's, faster, so the fastest goes first.</summary>
    public async Task<string> RequireToolAsync(Guid player, string tool, CancellationToken cancellationToken)
    {
        var held = await equipment.ToolsAsync(player, cancellationToken);
        if (tool == Items.Axe && Items.BestSaw(held) is { } saw)
        {
            return saw;
        }

        if (tool == Items.Pickaxe && Items.Pickaxes.Where(p => held.Contains(p.Key)).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault() is { } best)
        {
            return best;
        }

        return held.Contains(tool) ? tool : throw new GameException("wrong_tool", $"You need {Items.Names[tool]} in your hand.");
    }

    /// <summary>Gives what was gathered and the experience for it, for each of <paramref name="times"/> objects gathered at once.</summary>
    private async Task<Harvest> GiveAsync(
        Guid player, TileObject obj, IReadOnlyList<(string Item, int Quantity)> items, CancellationToken cancellationToken, int times = 1)
    {
        foreach (var (item, quantity) in items)
        {
            await inventory.ReceiveAsync(player, item, quantity, cancellationToken);
        }

        await stats.RecordAsync(player, items.Select(i => ProductionEntry.Made(i.Item, i.Quantity, time.GetUtcNow())).ToList(), cancellationToken);
        var xp = await progress.AddXpAsync(player, HarvestRules.XpPerHarvest * times, cancellationToken);
        return new Harvest(obj, items, xp);
    }

    /// <summary>Fails with <c>not_owner</c> unless the player owns the plot, or may do <paramref name="right"/>
    /// there as a member of the company the owner brought it into.</summary>
    public async Task<Biome> RequireOwnerAsync(
        Guid player, int x, int y, CancellationToken cancellationToken, PlotRight right = PlotRight.Build)
    {
        if (!(await RightsAsync(player, x, y, cancellationToken)).Contains(right))
        {
            throw new GameException("not_owner", "This is not your plot.");
        }

        // The owner's plot tax is paid whenever the plot is entered; while it is owed, nothing is built there.
        if (right == PlotRight.Build)
        {
            await taxes.RequirePaidAsync(x, y, cancellationToken);
        }
        else if (right == PlotRight.Visit)
        {
            await taxes.SettlePlotAsync(x, y, cancellationToken);
        }

        return map.BiomeAt(x, y);
    }

    /// <summary>Everything for the owner; for anyone else what their role in the plot's company allows, if anything,
    /// and on a public plot at least a visit.</summary>
    public async Task<IReadOnlySet<PlotRight>> RightsAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        if (!map.Contains(x, y))
        {
            throw new GameException("bad_plot", "There is no such plot.");
        }

        bool open;
        await using (var command = await db.CommandAsync("SELECT owner_id, public FROM world.plots WHERE x = @x AND y = @y", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return NoRights;
            }

            if (reader.GetGuid(0) == player)
            {
                return AllRights;
            }

            open = reader.GetBoolean(1);
        }

        var rights = new HashSet<PlotRight>();
        foreach (var company in companies)
        {
            rights.UnionWith(await company.RightsAsync(player, x, y, cancellationToken));
        }

        rights.Remove(PlotRight.Owner);
        // Members of the plot's company may always come; anyone else only when the owner made it public, and then only to look,
        // or to help with what the owner asked (IPlotPermits).
        if (rights.Count > 0 || open || permits.For(player, x, y) is not null)
        {
            rights.Add(PlotRight.Visit);
        }

        return rights;
    }

    /// <summary>Lets anyone come onto the owner's plot to look around, or only the owner and their company.</summary>
    public async Task SetPublicAsync(Guid player, int x, int y, bool open, CancellationToken cancellationToken)
    {
        await RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Owner);
        await using var command = await db.CommandAsync("UPDATE world.plots SET public = @open WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("open", open);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static readonly IReadOnlySet<PlotRight> AllRights = Enum.GetValues<PlotRight>().ToHashSet();

    private static readonly IReadOnlySet<PlotRight> NoRights = new HashSet<PlotRight>();
}

public sealed record EnterRequest(int X, int Y);

public sealed record HarvestRequest(int X, int Y, int TileX, int TileY);

/// <summary>Enters a plot the player may visit: its tiles (gzip, base64) and where things stand. A visitor of a public plot
/// gets its rights without <c>work</c>, so the client lets them only walk about.</summary>
public sealed class EnterPlotHandler(IServiceScopeFactory scopes, WorldMap map, TimeProvider time) : IMessageHandler
{
    public string Type => "plot.enter";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<EnterRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var rights = await scope.ServiceProvider.GetRequiredService<PlotService>()
            .RightsAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        // A fire since the last visit burns its trees before anyone sees them; only those who may enter start the watch.
        var fire = rights.Contains(PlotRight.Work)
            ? await scope.ServiceProvider.GetRequiredService<FireService>().CatchUpAsync(request.X, request.Y, cancellationToken)
            : null;
        var tiles = await scope.ServiceProvider.GetRequiredService<PlotService>()
            .TilesAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        var tools = await scope.ServiceProvider.GetRequiredService<IEquipment>().ToolsAsync(client.RequireAccount(), cancellationToken);
        var fields = await scope.ServiceProvider.GetRequiredService<FarmService>().FieldsAsync(request.X, request.Y, cancellationToken);
        var trees = await scope.ServiceProvider.GetRequiredService<ForestService>().TreesAsync(request.X, request.Y, cancellationToken);
        var animals = await scope.ServiceProvider.GetRequiredService<HusbandryService>().AnimalsAsync(request.X, request.Y, cancellationToken);
        var biome = map.BiomeAt(request.X, request.Y);
        var mines = await scope.ServiceProvider.GetRequiredService<MineService>().MinesAsync(request.X, request.Y, biome, cancellationToken);
        var objects = await scope.ServiceProvider.GetRequiredService<PlotObjectService>().ObjectsAsync(request.X, request.Y, cancellationToken);
        var permit = rights.Contains(PlotRight.Build)
            ? null
            : scope.ServiceProvider.GetRequiredService<PlotPermits>().For(client.RequireAccount(), request.X, request.Y);
        var pests = await scope.ServiceProvider.GetRequiredService<PestService>().StateAsync(request.X, request.Y, cancellationToken);
        var smog = await scope.ServiceProvider.GetRequiredService<SmogService>().LevelAsync(request.X, request.Y, cancellationToken);
        var certificate = await scope.ServiceProvider.GetRequiredService<ForestLedger>().JsonAsync(client.RequireAccount(), cancellationToken);
        var (likes, liked) = await scope.ServiceProvider.GetRequiredService<LikeService>()
            .LikesAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        var cities = scope.ServiceProvider.GetRequiredService<CityService>();
        object? city = null;
        if (cities.TownOf(request.X, request.Y) is { } town)
        {
            var level = CityRules.Level(await cities.DeliveredAsync(town, cancellationToken));
            city = new { name = town.Name, population = town.Population, level, levelName = CityRules.LevelNames[level] };
        }

        object? county = cities.CountyOf(request.X, request.Y) is { } seat ? new { name = seat.Name } : null;

        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal))
        {
            gzip.Write(tiles);
        }

        var (sx, sy, sw, sh) = PlotLayout.Station;
        return new Reply("plot", new
        {
            x = request.X,
            y = request.Y,
            biome = (int)biome,
            rights = rights.Order().Select(r => r.ToString().ToLowerInvariant()),
            // What the player came to set right on someone else's plot, with leave to (IPlotPermits); null otherwise.
            help = permit is null ? null : PlotPermits.Json(permit, objects),
            // How many players like the plot, and whether this one does.
            likes,
            liked,
            size = PlotLayout.Size,
            tileMeters = PlotLayout.TileMeters,
            tiles = Convert.ToBase64String(packed.ToArray()),
            railRow = PlotLayout.RailRow,
            station = new { x = sx, y = sy, width = sw, height = sh },
            spawn = new { x = PlotLayout.Spawn.X, y = PlotLayout.Spawn.Y },
            // The town a city plot belongs to, with its level; null elsewhere.
            city,
            // The town whose county the plot lies in (its mayor's laws hold here); null outside any.
            county,
            tools = tools.Select(t => new { id = t, name = Items.Names[t] }),
            harvestSeconds = HarvestRules.Time.TotalSeconds,
            // Rocks break and mines are worked this many percent faster here; better pickaxes are faster still.
            miningBonus = Mining.BonusPercent(biome),
            rockSeconds = HarvestRules.TimeFor(TileObject.Rock, Items.Pickaxe, biome).TotalSeconds,
            pickaxes = Items.Pickaxes,
            mineSeconds = (Mining.Time / Mining.Speed(biome)).TotalSeconds,
            mineUnits = Mining.Units,
            mines = mines.Select(MineHandler.MineJson),
            chopSeconds = HarvestRules.ChopTime.TotalSeconds,
            sawSeconds = HarvestRules.SawTime.TotalSeconds,
            // How long each saw takes over a tree, and how many trees next to it it fells at the same cut.
            saws = Items.Saws.ToDictionary(s => s.Key, s => new { seconds = (HarvestRules.ChopTime * s.Value.Time).TotalSeconds, fells = s.Value.Fells }),
            treeGrowSeconds = Forestry.GrowTime(biome)?.TotalSeconds,
            trees = trees.Select(PlantTreeHandler.TreeJson),
            // The kinds of tree with their saplings and growing times here, the bark beetle in a monoculture planted by hand,
            // the last forest fire (if recent) with what keeps fires small, and the player's forest certificate.
            treeKinds = PlantTreeHandler.KindsJson(biome),
            pests = PestService.Json(pests),
            fire = fire is null ? null : FireService.Json(fire),
            fires = FireService.RulesJson(biome),
            certificate,
            mushroomRegrowSeconds = Forestry.MushroomRegrow.TotalSeconds,
            objects = objects.Select(PlaceOnPlotHandler.ObjectJson),
            placeable = PlotObjects.Placeable,
            fertile = Farming.Fertile(biome),
            // The steppe and the desert can be tilled where a sprinkler waters, sprinklerReach tiles from it each way.
            irrigable = Farming.Irrigable(biome),
            sprinklerReach = Farming.SprinklerReach,
            sprinklerSpeed = Farming.SprinklerSpeed,
            // The smog over the plot: its own smoke, what it breathes from the plots around, and how fast crops grow in it.
            smog = SmogService.Json(smog),
            fields = fields.Select(FieldHandler.FieldJson),
            pasture = Husbandry.Pasture(biome),
            husbandryBonus = Husbandry.BonusPercent(biome),
            animals = animals.Select(AnimalHandler.AnimalJson),
            animalKinds = AnimalHandler.KindsJson(biome),
            feed = Husbandry.AnyFeed.Select(f => new { id = f, name = Items.Names[f] }),
            feedPerMeal = Husbandry.FeedPerMeal,
            crops = Farming.Crops.Select(c => new
            {
                id = c.Item,
                name = Items.Names[c.Item],
                growSeconds = c.Grow.TotalSeconds,
                yield = c.Yield,
                xp = c.Xp,
            }),
            // One cast of the rod here, faster on the coast and the lakeshore, and the storm blowing now or the next one.
            fishSeconds = Coast.FishTime(biome).TotalSeconds,
            fishingBonus = Coast.BonusPercent(biome),
            storm = StormJson(time.GetUtcNow()),
            storms = StormsJson(time.GetUtcNow(), 24),
            // The sun over every plot (a day of dayMs from epoch, the first daylightMs of it light), and on sand the sandstorms to come.
            sun = new
            {
                epoch = SeaWeather.Epoch.ToUnixTimeMilliseconds(),
                dayMs = (long)Sun.DayLength.TotalMilliseconds,
                daylightMs = (long)Sun.Daylight.TotalMilliseconds,
            },
            sandstorms = Oasis.Sandy((int)biome) ? SandstormsJson(time.GetUtcNow(), 12) : [],
            // Where crops grow: the rain (crops grow rainSpeed times as fast) and the droughts (droughtSpeed) to come.
            fieldWeather = Farming.Fertile(biome) ? FieldWeatherJson(time.GetUtcNow(), 12) : null,
            // On the glacier: the blizzards to come (turbines stop, ice freezes again, avalanches leave snow), the last one's window
            // for working out where its snow lies, and the frost that slows machines no heater warms.
            blizzards = biome == Biome.Ice ? BlizzardsJson(time.GetUtcNow(), 8) : [],
            frost = Frost.Freezes((int)biome) ? new { speed = Frost.Speed, heaterReach = Frost.HeaterReach } : null,
            bridgeBeams = PlotLayout.BridgeBeams,
            // On the glacier and the tundra: the nights the aurora shines, when tourists pay three times as much.
            auroras = Aurora.Seen((int)biome) ? AurorasJson(time.GetUtcNow(), 6) : [],
            serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    public static object StormJson(DateTimeOffset now)
    {
        var (start, end) = SeaWeather.Next(now);
        return new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() };
    }

    /// <summary>The storm blowing now (if any) and the next ones, <paramref name="count"/> in all, for the client's forecast.</summary>
    public static IReadOnlyList<object> StormsJson(DateTimeOffset now, int count)
    {
        var storms = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end) = SeaWeather.Next(at);
            storms.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });
            at = end;
        }

        return storms;
    }

    /// <summary>The rain or the drought now (if any) and the next ones, <paramref name="count"/> in all, with how fast crops grow in them.</summary>
    public static object FieldWeatherJson(DateTimeOffset now, int count)
    {
        var spells = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end, kind) = FieldWeather.Next(at);
            spells.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds(), kind = kind == FieldWeather.Kind.Rain ? "rain" : "drought" });
            at = end;
        }

        return new { rainSpeed = FieldWeather.RainSpeed, droughtSpeed = FieldWeather.DroughtSpeed, spells };
    }

    /// <summary>The blizzard blowing now (if any) and the next ones, <paramref name="count"/> in all.</summary>
    public static IReadOnlyList<object> BlizzardsJson(DateTimeOffset now, int count)
    {
        var storms = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end) = Blizzard.Next(at);
            storms.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });
            at = end;
        }

        return storms;
    }

    /// <summary>The aurora shining now (if it does) and the next ones, <paramref name="count"/> in all.</summary>
    public static IReadOnlyList<object> AurorasJson(DateTimeOffset now, int count)
    {
        var nights = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end) = Aurora.Next(at);
            nights.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });
            at = end;
        }

        return nights;
    }

    /// <summary>The sandstorm blowing now (if any) and the next ones, <paramref name="count"/> in all.</summary>
    public static IReadOnlyList<object> SandstormsJson(DateTimeOffset now, int count)
    {
        var storms = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end) = SandWeather.Next(at);
            storms.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });
            at = end;
        }

        return storms;
    }
}

public sealed class HarvestHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    private const string LastHarvest = "world.lastHarvest";

    public string Type => "plot.harvest";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<HarvestRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var harvest = await scope.ServiceProvider.GetRequiredService<PlotService>().HarvestAsync(
            client.RequireAccount(), request.X, request.Y, request.TileX, request.TileY, work => CheckPace(client, time, work), cancellationToken);
        Swung(client, time);
        return new Reply("plot.harvested", new
        {
            tileX = request.TileX,
            tileY = request.TileY,
            // What was gathered, e.g. "bridge" for a bridge laid over a crevasse.
            @object = harvest.Object.ToString().ToLowerInvariant(),
            items = harvest.Items.Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
            // The trees next to it that a saw felled at the same cut.
            felled = (harvest.Felled ?? []).Select(t => new { tileX = t.X, tileY = t.Y }),
            xp = new { xp = harvest.Xp.Xp, level = harvest.Xp.Level, leveledUp = harvest.Xp.LeveledUp },
        });
    }

    /// <summary>Refuses work that ends before it could have been done since the last one ended.</summary>
    public static void CheckPace(ClientContext client, TimeProvider time, TimeSpan work)
    {
        // A small tolerance for network jitter between the client's swing and the request.
        if (client.Items.GetValueOrDefault(LastHarvest) is DateTimeOffset last && time.GetUtcNow() - last < work * 0.8)
        {
            throw new GameException("too_fast", "Wait until the last swing is done.");
        }
    }

    public static void Swung(ClientContext client, TimeProvider time) => client.Items[LastHarvest] = time.GetUtcNow();
}
