using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Contracts.Migrations
{
    /// <inheritdoc />
    public partial class IndexesForOpenAndFinishedOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_contracts_issuer_id_id",
                schema: "contracts",
                table: "contracts");

            migrationBuilder.CreateIndex(
                name: "IX_contracts_board",
                schema: "contracts",
                table: "contracts",
                column: "id",
                filter: "closed_at IS NULL AND contractor_id IS NULL AND issuer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_contracts_closed_contractor_id_closed_at",
                schema: "contracts",
                table: "contracts",
                columns: new[] { "contractor_id", "closed_at" },
                filter: "closed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_contracts_closed_issuer_id_closed_at",
                schema: "contracts",
                table: "contracts",
                columns: new[] { "issuer_id", "closed_at" },
                filter: "closed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_contracts_open_issuer_id_id",
                schema: "contracts",
                table: "contracts",
                columns: new[] { "issuer_id", "id" },
                filter: "closed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_contracts_board",
                schema: "contracts",
                table: "contracts");

            migrationBuilder.DropIndex(
                name: "IX_contracts_closed_contractor_id_closed_at",
                schema: "contracts",
                table: "contracts");

            migrationBuilder.DropIndex(
                name: "IX_contracts_closed_issuer_id_closed_at",
                schema: "contracts",
                table: "contracts");

            migrationBuilder.DropIndex(
                name: "IX_contracts_open_issuer_id_id",
                schema: "contracts",
                table: "contracts");

            migrationBuilder.CreateIndex(
                name: "IX_contracts_issuer_id_id",
                schema: "contracts",
                table: "contracts",
                columns: new[] { "issuer_id", "id" });
        }
    }
}
