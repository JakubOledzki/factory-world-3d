using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class HarvestSquares : Migration
    {
        // Reads the squares back one row per gathered tile, as the table was before.
        private const string TilesView =
            """
            CREATE VIEW world.plot_harvest_tiles AS
            SELECT h.x, h.y, h.square % 16 * 16 + (c.cell - 1) % 16 AS tile_x, h.square / 16 * 16 + (c.cell - 1) / 16 AS tile_y, c.at AS harvested_at
            FROM world.plot_harvests h CROSS JOIN LATERAL unnest(h.harvested_at) WITH ORDINALITY AS c(at, cell)
            WHERE c.at IS NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A row per gathered tile becomes a row per square of 16 x 16 tiles holding when each of its tiles was gathered.
            migrationBuilder.Sql(
                """
                ALTER TABLE world.plot_harvests RENAME TO plot_harvests_by_tile;
                ALTER TABLE world.plot_harvests_by_tile RENAME CONSTRAINT "PK_plot_harvests" TO "PK_plot_harvests_by_tile";
                CREATE TABLE world.plot_harvests (
                    x integer NOT NULL,
                    y integer NOT NULL,
                    square smallint NOT NULL,
                    harvested_at timestamp with time zone[] NOT NULL,
                    CONSTRAINT "PK_plot_harvests" PRIMARY KEY (x, y, square)
                );
                INSERT INTO world.plot_harvests (x, y, square, harvested_at)
                SELECT s.x, s.y, s.square, ARRAY(SELECT s.ats[array_position(s.cells, g)] FROM generate_series(1, s.last) AS g ORDER BY g)
                FROM (
                    SELECT x, y, (tile_y / 16 * 16 + tile_x / 16)::smallint AS square, array_agg(tile_y % 16 * 16 + tile_x % 16 + 1) AS cells,
                        array_agg(harvested_at) AS ats, max(tile_y % 16 * 16 + tile_x % 16 + 1) AS last
                    FROM world.plot_harvests_by_tile GROUP BY x, y, square
                ) s;
                DROP TABLE world.plot_harvests_by_tile;
                """);
            migrationBuilder.Sql(TilesView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE world.plot_harvests_by_tile (
                    x integer NOT NULL,
                    y integer NOT NULL,
                    tile_x integer NOT NULL,
                    tile_y integer NOT NULL,
                    harvested_at timestamp with time zone NOT NULL DEFAULT now(),
                    CONSTRAINT "PK_plot_harvests_by_tile" PRIMARY KEY (x, y, tile_x, tile_y)
                );
                INSERT INTO world.plot_harvests_by_tile SELECT x, y, tile_x, tile_y, harvested_at FROM world.plot_harvest_tiles;
                DROP VIEW world.plot_harvest_tiles;
                DROP TABLE world.plot_harvests;
                ALTER TABLE world.plot_harvests_by_tile RENAME TO plot_harvests;
                ALTER TABLE world.plot_harvests RENAME CONSTRAINT "PK_plot_harvests_by_tile" TO "PK_plot_harvests";
                """);
        }
    }
}
