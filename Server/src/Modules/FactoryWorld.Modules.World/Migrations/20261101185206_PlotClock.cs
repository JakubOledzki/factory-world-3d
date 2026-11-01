using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class PlotClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plot_clock",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_clock", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plot_pauses",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    paused = table.Column<TimeSpan>(type: "interval", nullable: false),
                    paused_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_pauses", x => new { x.x, x.y });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plot_clock",
                schema: "world");

            migrationBuilder.DropTable(
                name: "plot_pauses",
                schema: "world");
        }
    }
}
