using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Market.Migrations
{
    /// <inheritdoc />
    public partial class OpenAndClosedOfferIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_owner_id_id",
                schema: "market",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_listings_owner_id_id",
                schema: "market",
                table: "listings");

            migrationBuilder.CreateIndex(
                name: "IX_orders_closed_owner_id_closed_at",
                schema: "market",
                table: "orders",
                columns: new[] { "owner_id", "closed_at" },
                filter: "closed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_orders_open_owner_id_id",
                schema: "market",
                table: "orders",
                columns: new[] { "owner_id", "id" },
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listings_closed_owner_id_closed_at",
                schema: "market",
                table: "listings",
                columns: new[] { "owner_id", "closed_at" },
                filter: "closed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listings_open_owner_id_id",
                schema: "market",
                table: "listings",
                columns: new[] { "owner_id", "id" },
                filter: "closed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_closed_owner_id_closed_at",
                schema: "market",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_open_owner_id_id",
                schema: "market",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_listings_closed_owner_id_closed_at",
                schema: "market",
                table: "listings");

            migrationBuilder.DropIndex(
                name: "IX_listings_open_owner_id_id",
                schema: "market",
                table: "listings");

            migrationBuilder.CreateIndex(
                name: "IX_orders_owner_id_id",
                schema: "market",
                table: "orders",
                columns: new[] { "owner_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_listings_owner_id_id",
                schema: "market",
                table: "listings",
                columns: new[] { "owner_id", "id" });
        }
    }
}
