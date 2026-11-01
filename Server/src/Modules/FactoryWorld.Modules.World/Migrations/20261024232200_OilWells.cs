using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class OilWells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oil_fields",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    field = table.Column<short>(type: "smallint", nullable: false),
                    pumped = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oil_fields", x => new { x.x, x.y, x.field });
                });

            migrationBuilder.CreateTable(
                name: "plot_wells",
                schema: "world",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    stored = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    wear = table.Column<int>(type: "integer", nullable: false),
                    gaskets = table.Column<int>(type: "integer", nullable: false),
                    leaking = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_wells", x => x.object_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plot_wells_owner_id",
                schema: "world",
                table: "plot_wells",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_plot_wells_x_y",
                schema: "world",
                table: "plot_wells",
                columns: new[] { "x", "y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oil_fields",
                schema: "world");

            migrationBuilder.DropTable(
                name: "plot_wells",
                schema: "world");
        }
    }
}
