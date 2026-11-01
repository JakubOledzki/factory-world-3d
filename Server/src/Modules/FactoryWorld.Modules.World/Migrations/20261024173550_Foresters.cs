using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class Foresters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plot_foresters",
                schema: "world",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wood = table.Column<int>(type: "integer", nullable: false),
                    busy_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_foresters", x => x.object_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plot_foresters_owner_id",
                schema: "world",
                table: "plot_foresters",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_plot_foresters_x_y",
                schema: "world",
                table: "plot_foresters",
                columns: new[] { "x", "y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plot_foresters",
                schema: "world");
        }
    }
}
