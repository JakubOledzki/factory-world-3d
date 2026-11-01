using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>A version of <see cref="Footprints.Sizes"/> every plot was brought in line with.</summary>
public sealed class FootprintVersionRow
{
    public int Version { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>Where the objects of one plot stand once their kinds grew (see <see cref="FootprintFit.Fit"/>).</summary>
/// <param name="Moved">Objects whose tile changes, with the tile they stand on now.</param>
/// <param name="Refunded">Objects with no room left, which go back to whoever put them there.</param>
public sealed record FootprintPlan(IReadOnlyList<(PlotObject Object, int TileX, int TileY)> Moved, IReadOnlyList<PlotObject> Refunded);

/// <summary>
/// Brings the objects of a plot built before machines grew (<see cref="Footprints"/>) in line: an object whose new square fits where it
/// stands stays; one that does not is moved as little as it takes, so that its square still covers the tile it stood on (so belts and
/// inserters next to it still reach it); one with no room either way goes back to the owner's inventory with all it held,
/// as does one of a kind that may no longer stand on the plot at all. Objects on one tile never move.
/// </summary>
public static class FootprintFit
{
    /// <param name="ground">Whether a tile is ground anything may stand on: inside the plot, free of nature, fields, trees and animals, off the spawn.</param>
    /// <param name="allowed">Whether an object of a kind may stand on the plot at all (city-only kinds elsewhere may not); everything by default.</param>
    public static FootprintPlan Fit(IReadOnlyList<PlotObject> objects, Func<int, int, bool> ground, Func<string, bool>? allowed = null)
    {
        var refunded = objects.Where(o => allowed?.Invoke(o.Kind) == false).ToList();
        objects = objects.Except(refunded).ToList();
        var taken = new HashSet<(int, int)>();
        foreach (var o in objects.Where(o => Footprints.Size(o.Kind) == 1))
        {
            taken.Add((o.TileX, o.TileY));
        }

        // The largest first, where they stand; then whatever did not fit, moved; the rest has no room.
        var sized = objects.Where(o => Footprints.Size(o.Kind) > 1).OrderByDescending(o => Footprints.Size(o.Kind)).ThenBy(o => o.Id).ToList();
        var left = new List<PlotObject>();
        foreach (var o in sized)
        {
            if (Fits(o.Kind, o.TileX, o.TileY))
            {
                Take(o.Kind, o.TileX, o.TileY);
            }
            else
            {
                left.Add(o);
            }
        }

        var moved = new List<(PlotObject, int, int)>();
        foreach (var o in left)
        {
            if (Anchors(o).Where(a => Fits(o.Kind, a.X, a.Y)).Select(a => ((int X, int Y)?)a).FirstOrDefault() is { } at)
            {
                Take(o.Kind, at.X, at.Y);
                moved.Add((o, at.X, at.Y));
            }
            else
            {
                refunded.Add(o);
            }
        }

        return new FootprintPlan(moved, refunded);

        bool Fits(string kind, int x, int y) => Footprints.Tiles(kind, x, y).All(t => ground(t.X, t.Y) && !taken.Contains(t));

        void Take(string kind, int x, int y) => taken.UnionWith(Footprints.Tiles(kind, x, y));
    }

    /// <summary>The tiles an object may stand on so that its square still covers its old tile, the nearest first.</summary>
    private static IEnumerable<(int X, int Y)> Anchors(PlotObject o)
    {
        var (low, high) = (Footprints.Low(o.Kind), Footprints.High(o.Kind));
        var anchors = new List<(int X, int Y)>();
        for (var dy = -high; dy <= low; dy++)
        {
            for (var dx = -high; dx <= low; dx++)
            {
                if ((dx, dy) != (0, 0))
                {
                    anchors.Add((o.TileX + dx, o.TileY + dy));
                }
            }
        }

        return anchors.OrderBy(a => Math.Abs(a.X - o.TileX) + Math.Abs(a.Y - o.TileY)).ThenBy(a => a.Y).ThenBy(a => a.X);
    }
}

/// <summary>Runs <see cref="FootprintFit"/> over every plot once for each <see cref="Footprints.Version"/>, at startup: version 2 also
/// took assembling machines, which stand in a city only since, off plots elsewhere.</summary>
public sealed class FootprintMigration(
    GameDb db, PlotService plots, IInventory inventory, IEnumerable<IMachineContents> machines, IEnumerable<IPlotAutomation> automation,
    PlotObjectsQuery query, ILogger<FootprintMigration> logger)
{
    /// <summary>Brings every plot in line unless that was done for this version already; returns how many objects moved and went back.</summary>
    public async Task<(int Moved, int Refunded)> RunAsync(CancellationToken cancellationToken)
    {
        await using (var done = await db.CommandAsync("SELECT EXISTS (SELECT 1 FROM world.footprint_versions WHERE version >= @version)", cancellationToken))
        {
            done.Parameters.AddWithValue("version", Footprints.Version);
            if ((bool)(await done.ExecuteScalarAsync(cancellationToken))!)
            {
                return (0, 0);
            }
        }

        var built = new List<(int X, int Y, Biome Biome)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT DISTINCT o.x, o.y, p.biome FROM world.plot_objects o JOIN world.plots p ON p.x = o.x AND p.y = o.y
            WHERE o.kind = ANY(@sizedKinds) ORDER BY o.x, o.y
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("sizedKinds", Footprints.SizedKinds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                built.Add((reader.GetInt32(0), reader.GetInt32(1), (Biome)reader.GetInt32(2)));
            }
        }

        var (moved, refunded) = (0, 0);
        foreach (var (x, y, biome) in built)
        {
            var plan = await db.InTransactionAsync(() => PlotAsync(x, y, biome, cancellationToken), cancellationToken);
            (moved, refunded) = (moved + plan.Moved.Count, refunded + plan.Refunded.Count);
            foreach (var o in plan.Refunded)
            {
                logger.LogInformation("Gave {Kind} back to {Owner}: no room for it on plot ({X}, {Y}) any more", o.Kind, o.Owner, x, y);
            }
        }

        await using (var mark = await db.CommandAsync(
            "INSERT INTO world.footprint_versions (version, at) VALUES (@version, now()) ON CONFLICT DO NOTHING", cancellationToken))
        {
            mark.Parameters.AddWithValue("version", Footprints.Version);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        if (built.Count > 0)
        {
            logger.LogInformation("Machines grew on {Plots} plots: {Moved} moved, {Refunded} given back", built.Count, moved, refunded);
        }

        return (moved, refunded);
    }

    private async Task<FootprintPlan> PlotAsync(int x, int y, Biome biome, CancellationToken cancellationToken)
    {
        // The plot's machinery works up to now as it stood, before anything moves.
        foreach (var machinery in automation)
        {
            await machinery.CatchUpAsync(x, y, cancellationToken);
        }

        var objects = await query.ObjectsAsync(x, y, cancellationToken);
        var tiles = await plots.TilesAsync(x, y, biome, cancellationToken);
        var occupied = new HashSet<(int, int)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT tile_x, tile_y FROM world.plot_fields WHERE x = @x AND y = @y
            UNION ALL SELECT tile_x, tile_y FROM world.plot_trees WHERE x = @x AND y = @y
            UNION ALL SELECT tile_x, tile_y FROM world.plot_animals WHERE x = @x AND y = @y
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                occupied.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
        }

        var plan = FootprintFit.Fit(objects, (tx, ty) =>
            PlotLayout.Inside(tx, ty) && tiles[ty * PlotLayout.Size + tx] is (byte)TileObject.None or (byte)TileObject.Bridge
            && !occupied.Contains((tx, ty)) && (tx, ty) != PlotLayout.Spawn && (biome != Biome.City || CityRules.InBuildArea(tx, ty)),
            kind => biome == Biome.City || !Items.IsCityOnly(kind));
        foreach (var (o, tileX, tileY) in plan.Moved)
        {
            await using var update = await db.CommandAsync("UPDATE world.plot_objects SET tile_x = @tx, tile_y = @ty WHERE id = @id", cancellationToken);
            update.Parameters.AddWithValue("id", o.Id);
            update.Parameters.AddWithValue("tx", tileX);
            update.Parameters.AddWithValue("ty", tileY);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var o in plan.Refunded)
        {
            await using (var delete = await db.CommandAsync("DELETE FROM world.plot_objects WHERE id = @id", cancellationToken))
            {
                delete.Parameters.AddWithValue("id", o.Id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var machine in machines)
            {
                await machine.ReleaseAsync(o.Owner, o.Id, cancellationToken);
            }

            await inventory.AddAsync(o.Owner, o.Kind, 1, cancellationToken);
        }

        query.Forget();
        return plan;
    }
}
