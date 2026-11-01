using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.World;

/// <summary>
/// Coastal plots. A coast plot ends in the sea along its south edge and a lakeshore plot has a lake in
/// its middle; both are fished from the shore with a rod (never fished out) and by boats standing on the
/// shore (<see cref="Items.IsBoat"/>), 30% faster than anywhere else (<see cref="BonusPercent"/>). A boat
/// has the water of nine plots around it to share, so a region takes only so many (<see cref="RegionBoats"/>).
/// Shore buildings stand right next to the water (<see cref="OnShore"/>). Every day the sea throws a few
/// things up on the beach of a coast plot, three times as many after a storm (<see cref="Flotsam"/>).
/// </summary>
public static class Coast
{
    /// <summary>How much faster fishing goes on a coast or lakeshore plot, in percent.</summary>
    public static int BonusPercent(Biome biome) => biome is Biome.Coast or Biome.Lakeshore ? 30 : 0;

    /// <summary>One cast of the rod at the plain rate.</summary>
    public static readonly TimeSpan CastTime = TimeSpan.FromSeconds(5);

    /// <summary>How long a cast takes on a plot of the biome.</summary>
    public static TimeSpan FishTime(Biome biome) => CastTime / (1 + BonusPercent(biome) / 100.0);

    /// <summary>Most boats on one plot.</summary>
    public const int PlotBoats = 4;

    /// <summary>Most boats on the nine plots around one and on it: the sea has fish for so many, a lake for fewer.</summary>
    public static int RegionBoats(Biome biome) => biome == Biome.Coast ? 8 : 6;

    /// <summary>Things the sea throws up on a coast plot's beach on a calm day; three times as many after a storm.</summary>
    public const int FlotsamPerDay = 12;

    /// <summary>Chance of amber in a find, in percent; twice as high on a plot with a lighthouse.</summary>
    public const int AmberPercent = 1;

    /// <summary>Kinds that stand right next to the water, and on which biome's shore (null for any shore).</summary>
    public static readonly IReadOnlyDictionary<string, Biome?> OnShore = new Dictionary<string, Biome?>
    {
        [Items.FishingBoat] = Biome.Coast,
        [Items.RowingBoat] = Biome.Lakeshore,
        [Items.SaltPan] = Biome.Coast,
        [Items.TidalPlant] = Biome.Coast,
        [Items.OffshorePlatform] = Biome.Coast,
        [Items.Desalinator] = Biome.Coast,
        [Items.FishFarm] = null,
        [Items.Shipyard] = null,
        [Items.Port] = null,
        [Items.Breakwater] = null,
        // Trains roll onto the ferry from the sea shore.
        [Items.RailPort] = Biome.Coast,
    };

    public static bool HasWater(Biome biome) => biome is Biome.Coast or Biome.Lakeshore;

    /// <summary>The seed a plot's layout is generated from.</summary>
    public static uint Seed(int plotX, int plotY) => (uint)(plotX * 73856093) ^ (uint)(plotY * 19349663);

    /// <summary>The first row of the sea in column <paramref name="x"/> of a coast plot.</summary>
    public static int ShoreRow(uint seed, int x) =>
        PlotLayout.Size - 26 + (int)Math.Round(16 * (PlotLayout.Noise(seed + 101, x / 22.0, 3.5) - 0.5));

    /// <summary>The lake of a lakeshore plot: its centre and half its width and height, in tiles.</summary>
    public static (int X, int Y, int Rx, int Ry) Lake(uint seed) =>
        (PlotLayout.Size / 2 - 30 + (int)(PlotLayout.Hash(seed, 31, 7) % 60), 140 + (int)(PlotLayout.Hash(seed, 7, 31) % 30), 38, 26);

    /// <summary>Puts the sea or the lake on a generated plot, with a bare strip of shore around it.</summary>
    public static void AddWater(byte[] tiles, uint seed, Biome biome)
    {
        const int size = PlotLayout.Size;
        if (biome == Biome.Coast)
        {
            for (var x = 0; x < size; x++)
            {
                var shore = ShoreRow(seed, x);
                for (var y = shore - 2; y < size; y++)
                {
                    tiles[y * size + x] = (byte)(y >= shore ? TileObject.Water : TileObject.None);
                }
            }
        }
        else if (biome is Biome.Lakeshore or Biome.Oasis)
        {
            var (cx, cy, rx, ry) = biome == Biome.Oasis ? Pond(seed) : Lake(seed);
            for (var y = cy - ry * 2; y <= cy + ry * 2; y++)
            {
                for (var x = cx - rx * 2; x <= cx + rx * 2; x++)
                {
                    if (!PlotLayout.Inside(x, y))
                    {
                        continue;
                    }

                    double dx = (x - cx) / (double)rx, dy = (y - cy) / (double)ry;
                    var edge = 1 + 0.35 * (PlotLayout.Noise(seed + 211, x / 9.0, y / 9.0) - 0.5);
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < edge)
                    {
                        tiles[y * size + x] = (byte)TileObject.Water;
                    }
                    else if (d < edge + 0.08)
                    {
                        tiles[y * size + x] = (byte)TileObject.None;
                    }
                }
            }
        }
    }

    /// <summary>The pond of an oasis: smaller than a lake, in the middle of the plot.</summary>
    public static (int X, int Y, int Rx, int Ry) Pond(uint seed) =>
        (PlotLayout.Size / 2 - 15 + (int)(PlotLayout.Hash(seed, 31, 7) % 30), 110 + (int)(PlotLayout.Hash(seed, 7, 31) % 40), 16, 11);

    /// <summary>Whether a tile touches the water, corners included.</summary>
    public static bool NextToWater(byte[] tiles, int tileX, int tileY)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var (x, y) = (tileX + dx, tileY + dy);
                if ((dx != 0 || dy != 0) && PlotLayout.Inside(x, y) && tiles[y * PlotLayout.Size + x] == (byte)TileObject.Water)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>One fish for a roll of 0-99: herring and cod in the sea, carp and pike in a lake.</summary>
    public static IReadOnlyList<(string Item, int Quantity)> Catch(Biome biome, int roll) => biome == Biome.Coast
        ? [(roll < 65 ? Items.Herring : Items.Cod, 1)]
        : [(roll < 60 ? Items.Carp : Items.Pike, 1)];

    /// <summary>Where the sea throws things up on day <paramref name="day"/>: free sand just above the water.</summary>
    public static IReadOnlyList<(int X, int Y)> Flotsam(int plotX, int plotY, Biome biome, byte[] tiles, int day, bool afterStorm)
    {
        if (biome != Biome.Coast)
        {
            return [];
        }

        var seed = Seed(plotX, plotY);
        var daySeed = seed ^ (uint)day * 0x9e3779b9u;
        var count = FlotsamPerDay * (afterStorm ? 3 : 1);
        var found = new HashSet<(int, int)>();
        for (var i = 0; i < count * 2 && found.Count < count; i++)
        {
            var x = (int)(PlotLayout.Hash(daySeed, i, 1) % PlotLayout.Size);
            var y = ShoreRow(seed, x) - 1 - (int)(PlotLayout.Hash(daySeed, i, 2) % 2);
            if (PlotLayout.Inside(x, y) && tiles[y * PlotLayout.Size + x] == (byte)TileObject.None)
            {
                found.Add((x, y));
            }
        }

        return found.ToList();
    }

    /// <summary>What one find on the beach gives, for a roll of 0-99.</summary>
    public static IReadOnlyList<(string Item, int Quantity)> Find(int roll, bool lighthouse) =>
        roll >= 100 - AmberPercent * (lighthouse ? 2 : 1) ? [(Items.Amber, 1)]
        : roll < 35 ? [(Items.Wood, 2)]
        : roll < 65 ? [(Items.Seaweed, 2)]
        : roll < 80 ? [(Items.Trash, 1)]
        : [(Items.WasteGlass, 1)];

    /// <summary>Fails unless an object of <paramref name="kind"/> may stand on (tileX, tileY): a shore building
    /// right next to the water of its biome, a lighthouse on the coast.</summary>
    public static void RequireSite(string kind, int x, int y, Biome biome, int tileX, int tileY)
    {
        if (kind == Items.Lighthouse && biome != Biome.Coast)
        {
            throw new GameException("no_coast", "A lighthouse stands on the coast.");
        }

        if (!OnShore.TryGetValue(kind, out var shore))
        {
            return;
        }

        if (shore is { } only ? biome != only : !HasWater(biome))
        {
            throw new GameException(shore == Biome.Coast ? "no_coast" : shore == Biome.Lakeshore ? "no_lake" : "no_shore",
                shore == Biome.Coast ? "It stands on the sea coast." : shore == Biome.Lakeshore ? "It stands on a lakeshore." : "It stands by the sea or a lake.");
        }

        // A big one (the rail port) has the water along any edge of its ground.
        var tiles = PlotLayout.Generate(x, y, biome);
        if (!Footprints.Tiles(kind, tileX, tileY).Any(t => NextToWater(tiles, t.X, t.Y)))
        {
            throw new GameException("not_on_shore", "It stands right next to the water.");
        }
    }
}

/// <summary>A find on a coast plot's beach gathered on one day.</summary>
public sealed class BeachFind
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Day { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }
}

/// <summary>The beach of coast plots and the storms on the water: what the sea threw up today, who may fish in a storm.</summary>
public sealed class BeachService(GameDb db, TimeProvider time)
{
    /// <summary>Today's finds on the beach still lying there, where nothing stands.</summary>
    public async Task<IReadOnlyList<(int X, int Y)>> FindsAsync(int x, int y, Biome biome, byte[] tiles, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (biome != Biome.Coast)
        {
            return [];
        }

        var day = SeaWeather.Day(now);
        var finds = Coast.Flotsam(x, y, biome, tiles, day, SeaWeather.StormBefore(day));
        if (finds.Count == 0)
        {
            return finds;
        }

        var taken = new HashSet<(int, int)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT tile_x, tile_y, '' FROM world.beach_finds WHERE x = @x AND y = @y AND day = @day
            UNION ALL SELECT tile_x, tile_y, kind FROM world.plot_objects WHERE x = @x AND y = @y
            UNION ALL SELECT tile_x, tile_y, '' FROM world.plot_fields WHERE x = @x AND y = @y
            UNION ALL SELECT tile_x, tile_y, '' FROM world.plot_trees WHERE x = @x AND y = @y
            UNION ALL SELECT tile_x, tile_y, '' FROM world.plot_animals WHERE x = @x AND y = @y
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("day", day);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                foreach (var tile in Footprints.Tiles(reader.GetString(2), reader.GetInt32(0), reader.GetInt32(1)))
                {
                    taken.Add(tile);
                }
            }
        }

        return finds.Where(f => !taken.Contains(f)).ToList();
    }

    /// <summary>Picks up today's find on a tile of the beach (the caller made sure one lies there).</summary>
    public async Task<Harvest> GatherAsync(
        Guid player, int x, int y, int tileX, int tileY, Func<IReadOnlyList<(string Item, int Quantity)>, Task<Harvest>> give, CancellationToken cancellationToken)
    {
        await using (var insert = await db.CommandAsync(
            "INSERT INTO world.beach_finds (x, y, day, tile_x, tile_y) VALUES (@x, @y, @day, @tx, @ty) ON CONFLICT DO NOTHING", cancellationToken))
        {
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("day", SeaWeather.Day(time.GetUtcNow()));
            insert.Parameters.AddWithValue("tx", tileX);
            insert.Parameters.AddWithValue("ty", tileY);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("nothing_here", "This has already been gathered.");
            }
        }

        // Finds of days gone by are forgotten: the sea has taken what was left of them back.
        await using (var forget = await db.CommandAsync("DELETE FROM world.beach_finds WHERE x = @x AND y = @y AND day < @day", cancellationToken))
        {
            forget.Parameters.AddWithValue("x", x);
            forget.Parameters.AddWithValue("y", y);
            forget.Parameters.AddWithValue("day", SeaWeather.Day(time.GetUtcNow()));
            await forget.ExecuteNonQueryAsync(cancellationToken);
        }

        return await give(Coast.Find(Random.Shared.Next(100), await HasAsync(x, y, Items.Lighthouse, 0, cancellationToken)));
    }

    /// <summary>Fails with <c>storm</c> while a storm blows, unless the plot has a breakwater to fish from.</summary>
    public async Task RequireCalmAsync(int x, int y, CancellationToken cancellationToken)
    {
        if (SeaWeather.IsStorm(time.GetUtcNow()) && !await HasAsync(x, y, Items.Breakwater, 0, cancellationToken))
        {
            throw new GameException("storm", "A storm is blowing: nobody fishes without a breakwater.");
        }
    }

    /// <summary>Whether an object of <paramref name="kind"/> stands on the plot or, with <paramref name="around"/> 1, on one next to it.</summary>
    public async Task<bool> HasAsync(int x, int y, string kind, int around, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT EXISTS (SELECT 1 FROM world.plot_objects WHERE kind = @kind AND abs(x - @x) <= @r AND abs(y - @y) <= @r)", cancellationToken);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("r", around);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Fails when another boat would be too many for the plot or for the water of the plots around it.</summary>
    public async Task RequireRoomForBoatAsync(int x, int y, Biome biome, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT count(*) FILTER (WHERE x = @x AND y = @y), count(*)
            FROM world.plot_objects WHERE kind = ANY(@boats) AND abs(x - @x) <= 1 AND abs(y - @y) <= 1
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("boats", new[] { Items.FishingBoat, Items.RowingBoat });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.GetInt64(0) >= Coast.PlotBoats)
        {
            throw new GameException("too_many_boats", $"A plot has room for {Coast.PlotBoats} boats.");
        }

        if (reader.GetInt64(1) >= Coast.RegionBoats(biome))
        {
            throw new GameException("overfished", $"The water here is fished out: the plots around have {Coast.RegionBoats(biome)} boats already.");
        }
    }
}
