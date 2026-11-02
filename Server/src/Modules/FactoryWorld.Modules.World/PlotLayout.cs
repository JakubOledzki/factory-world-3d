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
