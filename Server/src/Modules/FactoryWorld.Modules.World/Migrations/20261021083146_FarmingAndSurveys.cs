using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class FarmingAndSurveys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plot_fields",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    crop = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    planted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_fields", x => new { x.x, x.y, x.tile_x, x.tile_y });
                });

            migrationBuilder.CreateTable(
                name: "surveys",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    oil = table.Column<short>(type: "smallint", nullable: false),
                    gas = table.Column<short>(type: "smallint", nullable: false),
                    surveyed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_surveys", x => new { x.x, x.y });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plot_fields",
                schema: "world");

            migrationBuilder.DropTable(
                name: "surveys",
                schema: "world");
        }
    }
}
