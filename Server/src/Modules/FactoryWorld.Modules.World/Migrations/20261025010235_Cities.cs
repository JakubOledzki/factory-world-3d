using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class Cities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_sales",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_sales", x => new { x.id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "shop_items",
                schema: "world",
                columns: table => new
                {
                    shop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shop_items", x => new { x.shop_id, x.item });
                });

            migrationBuilder.CreateTable(
                name: "shops",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    carry = table.Column<double>(type: "double precision", nullable: false),
                    sold_cents = table.Column<long>(type: "bigint", nullable: false),
                    sold_items = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shops", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "town_contributions",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivered_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_contributions", x => new { x.town_x, x.town_y, x.player_id });
                });

            migrationBuilder.CreateTable(
                name: "town_deliveries",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_deliveries", x => new { x.town_x, x.town_y, x.day, x.item });
                });

            migrationBuilder.CreateTable(
                name: "town_policies",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    mayor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tax_percent = table.Column<int>(type: "integer", nullable: false),
                    bonus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_policies", x => new { x.town_x, x.town_y, x.week });
                });

            migrationBuilder.CreateTable(
                name: "town_progress",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    delivered_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_progress", x => new { x.town_x, x.town_y });
                });

            migrationBuilder.CreateTable(
                name: "town_votes",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    voter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_votes", x => new { x.town_x, x.town_y, x.week, x.voter_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_sales",
                schema: "world");

            migrationBuilder.DropTable(
                name: "shop_items",
                schema: "world");

            migrationBuilder.DropTable(
                name: "shops",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_contributions",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_deliveries",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_policies",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_progress",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_votes",
                schema: "world");
        }
    }
}
