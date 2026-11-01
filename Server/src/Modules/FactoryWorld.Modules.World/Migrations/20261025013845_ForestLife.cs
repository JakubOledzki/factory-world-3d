using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class ForestLife : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "species",
                schema: "world",
                table: "plot_trees",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "pine");

            migrationBuilder.CreateTable(
                name: "forest_ledger",
                schema: "world",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    planted = table.Column<int>(type: "integer", nullable: false),
                    cut = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forest_ledger", x => new { x.player_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "hives",
                schema: "world",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    honey = table.Column<int>(type: "integer", nullable: false),
                    wax = table.Column<int>(type: "integer", nullable: false),
                    made = table.Column<int>(type: "integer", nullable: false),
                    progress = table.Column<double>(type: "double precision", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hives", x => x.object_id);
                });

            migrationBuilder.CreateTable(
                name: "plot_fires",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_x = table.Column<int>(type: "integer", nullable: false),
                    last_y = table.Column<int>(type: "integer", nullable: false),
                    last_burnt = table.Column<int>(type: "integer", nullable: false),
                    last_stopped = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_fires", x => new { x.x, x.y });
                });

            migrationBuilder.CreateTable(
                name: "plot_sprays",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_sprays", x => new { x.x, x.y });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forest_ledger",
                schema: "world");

            migrationBuilder.DropTable(
                name: "hives",
                schema: "world");

            migrationBuilder.DropTable(
                name: "plot_fires",
                schema: "world");

            migrationBuilder.DropTable(
                name: "plot_sprays",
                schema: "world");

            migrationBuilder.DropColumn(
                name: "species",
                schema: "world",
                table: "plot_trees");
        }
    }
}
