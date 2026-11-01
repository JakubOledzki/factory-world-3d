using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class Drills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plot_drills",
                schema: "world",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mine = table.Column<int>(type: "integer", nullable: false),
                    ores = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    waiting = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_drills", x => x.object_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plot_drills_owner_id",
                schema: "world",
                table: "plot_drills",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_plot_drills_x_y",
                schema: "world",
                table: "plot_drills",
                columns: new[] { "x", "y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plot_drills",
                schema: "world");
        }
    }
}
