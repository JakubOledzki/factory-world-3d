using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class ForesterTrees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "forester_id",
                schema: "world",
                table: "plot_trees",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_plot_trees_forester_id",
                schema: "world",
                table: "plot_trees",
                column: "forester_id");

            // Trees planted before trees knew their forester go to the nearest one whose forest covers them.
            migrationBuilder.Sql(
                """
                UPDATE world.plot_trees t SET forester_id = (
                    SELECT o.id FROM world.plot_objects o
                    WHERE o.x = t.x AND o.y = t.y
                      AND o.kind IN ('furniture_forester_hut', 'furniture_forester_lodge', 'furniture_forestry_station')
                      AND greatest(abs(o.tile_x - t.tile_x), abs(o.tile_y - t.tile_y))
                          <= CASE o.kind WHEN 'furniture_forester_hut' THEN 5 WHEN 'furniture_forester_lodge' THEN 6 ELSE 7 END
                    ORDER BY abs(o.tile_x - t.tile_x) + abs(o.tile_y - t.tile_y), o.id
                    LIMIT 1)
                WHERE t.forester_id IS NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_plot_trees_forester_id",
                schema: "world",
                table: "plot_trees");

            migrationBuilder.DropColumn(
                name: "forester_id",
                schema: "world",
                table: "plot_trees");
        }
    }
}
