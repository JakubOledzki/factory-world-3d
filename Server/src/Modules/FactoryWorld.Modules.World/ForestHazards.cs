using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.World;

/// <summary>
/// The bark beetle: where a player planted a monoculture (at least <see cref="MinTrees"/> trees by hand, <see cref="MonoculturePercent"/>%
/// or more of one kind), it eats <see cref="InfestedPercent"/>% of that kind every day, a different part each day. An infested tree
/// gives only a log when cut. A can of insecticide keeps the beetle off the plot for <see cref="SprayLasts"/>; a mixed forest never needs one.
/// Trees a forester planted are its own business and are not counted.
/// </summary>
public static class Pests
{
    public const int MinTrees = 30;

    public const int MonoculturePercent = 70;

    public const int InfestedPercent = 20;

    public static readonly TimeSpan SprayLasts = TimeSpan.FromDays(3);

    /// <summary>The kind the planted trees are a monoculture of, or null when they are mixed or too few.</summary>
    public static string? Monoculture(IReadOnlyCollection<string> species)
    {
        if (species.Count < MinTrees)
        {
            return null;
        }

        var (kind, count) = species.GroupBy(s => s).Select(g => (g.Key, g.Count())).MaxBy(g => g.Item2);
        return count * 100 >= species.Count * MonoculturePercent ? kind : null;
    }

    /// <summary>Whether the beetle is in the tree on (tileX, tileY) of plot (x, y) on the day of <paramref name="now"/>.</summary>
    public static bool Infested(int x, int y, int tileX, int tileY, DateTimeOffset now)
    {
        var day = (int)(now.ToUnixTimeSeconds() / 86_400);
        var seed = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(day * 83492791);
        return PlotLayout.Hash(seed, tileX, tileY) % 100 < InfestedPercent;
    }
}

/// <param name="Species">The kind the hand-planted trees are a monoculture of, or null.</param>
/// <param name="Infested">The trees the beetle is in today.</param>
public sealed record PestState(string? Species, int Trees, DateTimeOffset? SprayedUntil, IReadOnlyList<(int X, int Y)> Infested);

public sealed class PestService(GameDb db, TimeProvider time)
{
    public async Task<PestState> StateAsync(int x, int y, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var trees = new List<(int X, int Y, string Species)>();
        await using (var command = await db.CommandAsync(
            "SELECT tile_x, tile_y, species FROM world.plot_trees WHERE x = @x AND y = @y AND forester_id IS NULL", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                trees.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)));
            }
        }

        DateTimeOffset? sprayed = null;
        await using (var command = await db.CommandAsync("SELECT until FROM world.plot_sprays WHERE x = @x AND y = @y", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            if (await command.ExecuteScalarAsync(cancellationToken) is DateTime until && new DateTimeOffset(until, TimeSpan.Zero) > now)
            {
                sprayed = new DateTimeOffset(until, TimeSpan.Zero);
            }
        }

        var species = Pests.Monoculture(trees.Select(t => t.Species).ToList());
        var infested = species is null || sprayed is not null
            ? []
            : trees.Where(t => t.Species == species && Pests.Infested(x, y, t.X, t.Y, now)).Select(t => (t.X, t.Y)).ToList();
        return new PestState(species, trees.Count, sprayed, infested);
    }

    public static object Json(PestState state) => new
    {
        monoculture = state.Species,
        trees = state.Trees,
        minTrees = Pests.MinTrees,
        monoculturePercent = Pests.MonoculturePercent,
        sprayedUntil = state.SprayedUntil?.ToUnixTimeMilliseconds(),
        infested = state.Infested.Select(t => new { tileX = t.X, tileY = t.Y }),
    };
}

/// <summary>
/// Forest fires. On a dry day (<see cref="ChancePercent"/>% of days, at an hour of each plot's own) a fire breaks out in a tree of a
/// wooded plot and spreads from tree to touching tree, up to <see cref="Radius"/> tiles from where it started: a clearing or a firebreak
/// stops it. A fire watchtower on the plot sees it early, so it burns only <see cref="WatchtowerRadius"/> tiles far; trees within
/// <see cref="StationReach"/> tiles of a fire station never burn, and a fire starting there is put out at once. Burnt wild trees are gone
/// like cut ones, burnt planted ones too. Fires burn only while their plot is looked after: the first visit starts the watch.
/// </summary>
public static class Wildfires
{
    public const int ChancePercent = 15;

    public const int Radius = 12;

    public const int WatchtowerRadius = 4;

    public const int StationReach = 12;

    /// <summary>Fires missed while nobody came are caught up for this many days at most.</summary>
    public const int CatchUpDays = 7;

    /// <summary>How long the last fire is reported on entering the plot.</summary>
    public static readonly TimeSpan Reported = TimeSpan.FromDays(3);

    public static bool Burns(Biome biome) => biome is Biome.Forest or Biome.Lakeshore or Biome.Grassland or Biome.Farmland;

    /// <summary>When a fire breaks out on plot (x, y) on <paramref name="day"/> (days from 1970), or null on a wet day.</summary>
    public static DateTimeOffset? OnDay(int x, int y, long day)
    {
        var seed = (uint)(x * 2654435761) ^ (uint)(y * 40503) ^ 0x5eedf13eu;
        var h = PlotLayout.Hash(seed, (int)day, 7);
        return h % 100 < ChancePercent ? DateTimeOffset.UnixEpoch.AddDays(day).AddMinutes(h / 100 % (24 * 60)) : null;
    }

    /// <summary>The fires on plot (x, y) after <paramref name="after"/> up to <paramref name="upTo"/>, oldest first.</summary>
    public static IEnumerable<DateTimeOffset> Between(int x, int y, DateTimeOffset after, DateTimeOffset upTo)
    {
        var from = Math.Max(after.ToUnixTimeSeconds() / 86_400, upTo.ToUnixTimeSeconds() / 86_400 - CatchUpDays);
        for (var day = from; day <= upTo.ToUnixTimeSeconds() / 86_400; day++)
        {
            if (OnDay(x, y, day) is { } at && at > after && at <= upTo)
            {
                yield return at;
            }
        }
    }

    /// <summary>The trees a fire started at <paramref name="origin"/> burns: those touching it (corners too) within its reach.</summary>
    public static List<(int X, int Y)> Spread((int X, int Y) origin, IReadOnlySet<(int X, int Y)> trees, int radius, Func<(int X, int Y), bool> guarded)
    {
        var burnt = new List<(int, int)>();
        if (guarded(origin))
        {
            return burnt;
        }

        var seen = new HashSet<(int, int)> { origin };
        var queue = new Queue<(int X, int Y)>([origin]);
        while (queue.TryDequeue(out var at))
        {
            burnt.Add(at);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var next = (X: at.X + dx, Y: at.Y + dy);
                    if (Math.Abs(next.X - origin.X) <= radius && Math.Abs(next.Y - origin.Y) <= radius
                        && trees.Contains(next) && !guarded(next) && seen.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }
        }

        return burnt;
    }
}

/// <param name="Stopped">The fire station put it out before it spread.</param>
public sealed record ForestFire(DateTimeOffset At, int TileX, int TileY, int Burnt, bool Stopped);

public sealed class FireService(GameDb db, PlotService plots, WorldMap map, TimeProvider time)
{
    /// <summary>Burns what the fires since the last visit burnt and tells the last fire, if recent. Started by the first visit.</summary>
    public Task<ForestFire?> CatchUpAsync(int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var biome = map.BiomeAt(x, y);
            if (!Wildfires.Burns(biome))
            {
                return null;
            }

            var now = time.GetUtcNow();
            DateTimeOffset? checkedAt = null;
            ForestFire? last = null;
            await using (var select = await db.CommandAsync(
                "SELECT checked_at, last_at, last_x, last_y, last_burnt, last_stopped FROM world.plot_fires WHERE x = @x AND y = @y FOR UPDATE",
                cancellationToken))
            {
                select.Parameters.AddWithValue("x", x);
                select.Parameters.AddWithValue("y", y);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    checkedAt = reader.GetFieldValue<DateTimeOffset>(0);
                    if (!reader.IsDBNull(1))
                    {
                        last = new ForestFire(reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetBoolean(5));
                    }
                }
            }

            if (checkedAt is { } since)
            {
                foreach (var at in Wildfires.Between(x, y, since, now))
                {
                    last = await BurnAsync(x, y, biome, at, cancellationToken) ?? last;
                }
            }

            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO world.plot_fires (x, y, checked_at, last_at, last_x, last_y, last_burnt, last_stopped)
                VALUES (@x, @y, @now, @at, @tx, @ty, @burnt, @stopped)
                ON CONFLICT (x, y) DO UPDATE SET checked_at = @now, last_at = @at, last_x = @tx, last_y = @ty, last_burnt = @burnt, last_stopped = @stopped
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("x", x);
                upsert.Parameters.AddWithValue("y", y);
                upsert.Parameters.AddWithValue("now", now);
                upsert.Parameters.Add(new Npgsql.NpgsqlParameter("at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)last?.At ?? DBNull.Value });
                upsert.Parameters.AddWithValue("tx", last?.TileX ?? 0);
                upsert.Parameters.AddWithValue("ty", last?.TileY ?? 0);
                upsert.Parameters.AddWithValue("burnt", last?.Burnt ?? 0);
                upsert.Parameters.AddWithValue("stopped", last?.Stopped ?? false);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            return last is { } fire && now - fire.At <= Wildfires.Reported ? fire : null;
        }, cancellationToken);

    /// <summary>A fire at <paramref name="at"/>: it starts in one of the plot's trees and burns what it reaches. Null when no tree stands.</summary>
    private async Task<ForestFire?> BurnAsync(int x, int y, Biome biome, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var tiles = await plots.TilesAsync(x, y, biome, cancellationToken);
        var trees = new HashSet<(int X, int Y)>();
        for (var i = 0; i < tiles.Length; i++)
        {
            if (Trees.IsTree(tiles[i]))
            {
                trees.Add((i % PlotLayout.Size, i / PlotLayout.Size));
            }
        }

        var planted = (await ForestService.ReadAsync(db, x, y, cancellationToken)).Select(t => (t.TileX, t.TileY)).ToHashSet();
        trees.UnionWith(planted);
        if (trees.Count == 0)
        {
            return null;
        }

        var stations = new List<(int X, int Y)>();
        var watched = false;
        await using (var command = await db.CommandAsync(
            "SELECT kind, tile_x, tile_y FROM world.plot_objects WHERE x = @x AND y = @y AND kind = ANY(@kinds)", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("kinds", new[] { Items.Watchtower, Items.FireStation });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) == Items.FireStation)
                {
                    stations.Add((reader.GetInt32(1), reader.GetInt32(2)));
                }
                else
                {
                    watched = true;
                }
            }
        }

        var ordered = trees.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        var origin = ordered[(int)(PlotLayout.Hash((uint)at.ToUnixTimeSeconds(), x, y) % (uint)ordered.Count)];
        bool Guarded((int X, int Y) t) => stations.Any(s => Math.Abs(s.X - t.X) <= Wildfires.StationReach && Math.Abs(s.Y - t.Y) <= Wildfires.StationReach);
        var burnt = Wildfires.Spread(origin, trees, watched ? Wildfires.WatchtowerRadius : Wildfires.Radius, Guarded);
        var wild = burnt.Where(t => !planted.Contains(t)).ToList();
        if (wild.Count > 0)
        {
            await PlotHarvests.MarkManyAsync(db, x, y, wild, at, cancellationToken);
        }

        var lost = burnt.Where(planted.Contains).ToList();
        if (lost.Count > 0)
        {
            await using var delete = await db.CommandAsync(
                """
                DELETE FROM world.plot_trees WHERE x = @x AND y = @y AND (tile_x, tile_y) IN (SELECT * FROM unnest(@txs, @tys))
                """, cancellationToken);
            delete.Parameters.AddWithValue("x", x);
            delete.Parameters.AddWithValue("y", y);
            delete.Parameters.AddWithValue("txs", lost.Select(t => t.X).ToArray());
            delete.Parameters.AddWithValue("tys", lost.Select(t => t.Y).ToArray());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        return new ForestFire(at, origin.X, origin.Y, burnt.Count, burnt.Count == 0);
    }

    public static object Json(ForestFire fire) => new
    {
        at = fire.At.ToUnixTimeMilliseconds(),
        tileX = fire.TileX,
        tileY = fire.TileY,
        burnt = fire.Burnt,
        stopped = fire.Stopped,
    };

    public static object RulesJson(Biome biome) => new
    {
        burns = Wildfires.Burns(biome),
        chancePercent = Wildfires.ChancePercent,
        radius = Wildfires.Radius,
        watchtowerRadius = Wildfires.WatchtowerRadius,
        stationReach = Wildfires.StationReach,
    };
}
