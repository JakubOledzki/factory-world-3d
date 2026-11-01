using FactoryWorld.Modules.Abstractions;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>
/// The gathered tiles of plots. A plot is cut into squares of <see cref="Side"/> × <see cref="Side"/> tiles and each square is one
/// row of <c>world.plot_harvests</c>, whose array holds when each of its tiles was gathered (NULL where it was not), so a plot
/// gathered bare is a few hundred small rows instead of tens of thousands. <see cref="Gathered"/> unpacks a row; the view
/// <see cref="Tiles"/> reads them back one row per tile.
/// </summary>
public static class PlotHarvests
{
    /// <summary>Tiles along a side of one square.</summary>
    public const int Side = 16;

    /// <summary>Squares along a side of a plot.</summary>
    public const int Squares = (PlotLayout.Size + Side - 1) / Side;

    /// <summary>A view of <c>(x, y, tile_x, tile_y, harvested_at)</c>, one row per gathered tile.</summary>
    public const string Tiles = "world.plot_harvest_tiles";

    /// <summary>The square of a tile.</summary>
    public static short Square(int tileX, int tileY) => (short)(tileY / Side * Squares + tileX / Side);

    /// <summary>The place of a tile in its square's array, from 1 as in SQL.</summary>
    public static int Cell(int tileX, int tileY) => tileY % Side * Side + tileX % Side + 1;

    /// <summary>The tiles of <paramref name="square"/> gathered, and when, from its row's array.</summary>
    public static IEnumerable<(int TileX, int TileY, DateTimeOffset At)> Gathered(int square, DateTimeOffset?[] harvestedAt)
    {
        var (left, top) = (square % Squares * Side, square / Squares * Side);
        for (var i = 0; i < harvestedAt.Length; i++)
        {
            if (harvestedAt[i] is { } at)
            {
                yield return (left + i % Side, top + i / Side, at);
            }
        }
    }

    /// <summary>
    /// Records that a tile was gathered at <paramref name="at"/>; <paramref name="again"/> also when it was before, otherwise
    /// it is left as it was. False when it was gathered before and not recorded again.
    /// </summary>
    public static async Task<bool> MarkAsync(GameDb db, int x, int y, int tileX, int tileY, DateTimeOffset at, bool again, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO world.plot_harvests AS h (x, y, square, harvested_at)
            VALUES (@x, @y, @square, array_fill(NULL::timestamptz, ARRAY[@cell - 1]) || @at)
            ON CONFLICT (x, y, square) DO UPDATE SET harvested_at[@cell] = @at WHERE @again OR h.harvested_at[@cell] IS NULL
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("square", Square(tileX, tileY));
        command.Parameters.AddWithValue("cell", Cell(tileX, tileY));
        command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, at);
        command.Parameters.AddWithValue("again", again);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Records that <paramref name="tiles"/> were gathered at <paramref name="at"/>, or now by the database clock; those gathered before are left as they were.</summary>
    public static async Task MarkManyAsync(GameDb db, int x, int y, IReadOnlyCollection<(int X, int Y)> tiles, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        if (tiles.Count == 0)
        {
            return;
        }

        // Each square touched is written once: its new cells are filled in where they are still empty.
        await using var command = await db.CommandAsync(
            """
            WITH marked AS (
                SELECT t.square, array_agg(t.cell) AS cells, max(t.cell) AS last FROM unnest(@squares, @cells) AS t(square, cell) GROUP BY t.square
            )
            INSERT INTO world.plot_harvests AS h (x, y, square, harvested_at)
            SELECT @x, @y, m.square,
                ARRAY(SELECT CASE WHEN g = ANY(m.cells) THEN COALESCE(@at, now()) END FROM generate_series(1, m.last) AS g ORDER BY g)
            FROM marked m
            ON CONFLICT (x, y, square) DO UPDATE SET harvested_at = ARRAY(
                SELECT COALESCE(h.harvested_at[g], excluded.harvested_at[g])
                FROM generate_series(1, GREATEST(cardinality(h.harvested_at), cardinality(excluded.harvested_at))) AS g ORDER BY g)
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("squares", tiles.Select(t => Square(t.X, t.Y)).ToArray());
        command.Parameters.AddWithValue("cells", tiles.Select(t => Cell(t.X, t.Y)).ToArray());
        command.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, (object?)at ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
