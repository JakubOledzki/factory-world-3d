using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class ExportByRail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "export_chests",
                schema: "world",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    next_pickup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_pickup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_quantity = table.Column<long>(type: "bigint", nullable: false),
                    last_problem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_chests", x => x.object_id);
                });

            migrationBuilder.CreateTable(
                name: "export_items",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_items", x => new { x.x, x.y, x.item });
                });

            migrationBuilder.CreateTable(
                name: "export_posts",
                schema: "world",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    earned_cents = table.Column<long>(type: "bigint", nullable: false),
                    uses = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_posts", x => x.object_id);
                });

            migrationBuilder.CreateTable(
                name: "export_prices",
                schema: "world",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_prices", x => new { x.x, x.y, x.item });
                });

            migrationBuilder.CreateTable(
                name: "export_shipments",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_x = table.Column<int>(type: "integer", nullable: false),
                    from_y = table.Column<int>(type: "integer", nullable: false),
                    market_x = table.Column<int>(type: "integer", nullable: false),
                    market_y = table.Column<int>(type: "integer", nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false),
                    arrives_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_shipments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_export_chests_next_pickup_at",
                schema: "world",
                table: "export_chests",
                column: "next_pickup_at");

            migrationBuilder.CreateIndex(
                name: "IX_export_chests_x_y",
                schema: "world",
                table: "export_chests",
                columns: new[] { "x", "y" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_export_shipments_arrives_at",
                schema: "world",
                table: "export_shipments",
                column: "arrives_at");

            migrationBuilder.CreateIndex(
                name: "IX_export_shipments_from_x_from_y",
                schema: "world",
                table: "export_shipments",
                columns: new[] { "from_x", "from_y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "export_chests",
                schema: "world");

            migrationBuilder.DropTable(
                name: "export_items",
                schema: "world");

            migrationBuilder.DropTable(
                name: "export_posts",
                schema: "world");

            migrationBuilder.DropTable(
                name: "export_prices",
                schema: "world");

            migrationBuilder.DropTable(
                name: "export_shipments",
                schema: "world");
        }
    }
}
