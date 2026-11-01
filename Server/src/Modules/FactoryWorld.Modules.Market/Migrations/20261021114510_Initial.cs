using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Market.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "market");

            migrationBuilder.CreateTable(
                name: "listings",
                schema: "market",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    quantity_left = table.Column<long>(type: "bigint", nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listings", x => x.id);
                    table.CheckConstraint("ck_listings_price", "price_cents > 0");
                    table.CheckConstraint("ck_listings_quantity", "quantity_left >= 0 AND quantity_left <= quantity");
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "market",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    quantity_left = table.Column<long>(type: "bigint", nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_price", "price_cents > 0");
                    table.CheckConstraint("ck_orders_quantity", "quantity_left >= 0 AND quantity_left <= quantity");
                });

            migrationBuilder.CreateIndex(
                name: "IX_listings_item_id_price_cents",
                schema: "market",
                table: "listings",
                columns: new[] { "item_id", "price_cents" },
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listings_owner_id_id",
                schema: "market",
                table: "listings",
                columns: new[] { "owner_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_item_id_price_cents",
                schema: "market",
                table: "orders",
                columns: new[] { "item_id", "price_cents" },
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_orders_owner_id_id",
                schema: "market",
                table: "orders",
                columns: new[] { "owner_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "listings",
                schema: "market");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "market");
        }
    }
}
