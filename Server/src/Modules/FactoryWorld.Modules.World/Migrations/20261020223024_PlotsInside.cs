using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class PlotsInside : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plot_harvests",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_harvests", x => new { x.x, x.y, x.tile_x, x.tile_y });
                });

            migrationBuilder.CreateTable(
                name: "shipments",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    from_x = table.Column<int>(type: "integer", nullable: false),
                    from_y = table.Column<int>(type: "integer", nullable: false),
                    to_x = table.Column<int>(type: "integer", nullable: false),
                    to_y = table.Column<int>(type: "integer", nullable: false),
                    item = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    sender_id = table.Column<Guid>(type: "uuid", nullable: false),
                    arrives_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "station_items",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    item = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_items", x => new { x.x, x.y, x.item });
                });

            migrationBuilder.CreateIndex(
                name: "IX_shipments_from_x_from_y",
                schema: "world",
                table: "shipments",
                columns: new[] { "from_x", "from_y" });

            migrationBuilder.CreateIndex(
                name: "IX_shipments_to_x_to_y_arrives_at",
                schema: "world",
                table: "shipments",
                columns: new[] { "to_x", "to_y", "arrives_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plot_harvests",
                schema: "world");

            migrationBuilder.DropTable(
                name: "shipments",
                schema: "world");

            migrationBuilder.DropTable(
                name: "station_items",
                schema: "world");
        }
    }
}
