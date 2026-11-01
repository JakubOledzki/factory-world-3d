using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class TendersSuppliersMeteors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meteor_digs",
                schema: "world",
                columns: table => new
                {
                    slot = table.Column<long>(type: "bigint", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pieces = table.Column<long>(type: "bigint", nullable: false),
                    last_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meteor_digs", x => new { x.slot, x.player_id });
                });

            migrationBuilder.CreateTable(
                name: "meteors",
                schema: "world",
                columns: table => new
                {
                    slot = table.Column<long>(type: "bigint", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    iron_left = table.Column<long>(type: "bigint", nullable: false),
                    iridium_left = table.Column<long>(type: "bigint", nullable: false),
                    fell_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meteors", x => x.slot);
                    table.CheckConstraint("ck_meteors_left", "iron_left >= 0 AND iridium_left >= 0");
                });

            migrationBuilder.CreateTable(
                name: "tender_bids",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    bidder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false),
                    deposit_cents = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tender_bids", x => new { x.town_x, x.town_y, x.week, x.bidder_id });
                    table.CheckConstraint("ck_tender_bids_money", "price_cents > 0 AND deposit_cents > 0");
                });

            migrationBuilder.CreateTable(
                name: "town_tenders",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    item = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    cap_cents = table.Column<long>(type: "bigint", nullable: false),
                    closes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    winner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_cents = table.Column<long>(type: "bigint", nullable: false),
                    deposit_cents = table.Column<long>(type: "bigint", nullable: false),
                    delivered = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_tenders", x => new { x.town_x, x.town_y, x.week });
                    table.CheckConstraint("ck_town_tenders_delivered", "delivered >= 0 AND delivered <= quantity");
                    table.CheckConstraint("ck_town_tenders_money", "price_cents >= 0 AND deposit_cents >= 0");
                });

            migrationBuilder.CreateTable(
                name: "town_week_deliveries",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivered_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_week_deliveries", x => new { x.town_x, x.town_y, x.week, x.player_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_town_tenders_state",
                schema: "world",
                table: "town_tenders",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_town_week_deliveries_town_x_town_y_week_delivered_cents",
                schema: "world",
                table: "town_week_deliveries",
                columns: new[] { "town_x", "town_y", "week", "delivered_cents" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meteor_digs",
                schema: "world");

            migrationBuilder.DropTable(
                name: "meteors",
                schema: "world");

            migrationBuilder.DropTable(
                name: "tender_bids",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_tenders",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_week_deliveries",
                schema: "world");
        }
    }
}
