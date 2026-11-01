using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Contracts.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "contracts");

            migrationBuilder.CreateTable(
                name: "contracts",
                schema: "contracts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    issuer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issuer_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    contractor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    item_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    delivered = table.Column<long>(type: "bigint", nullable: false),
                    reward_cents = table.Column<long>(type: "bigint", nullable: false),
                    xp = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contracts", x => x.id);
                    table.CheckConstraint("ck_contracts_delivered", "delivered >= 0 AND delivered <= quantity");
                    table.CheckConstraint("ck_contracts_reward", "reward_cents >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_contracts_contractor_id",
                schema: "contracts",
                table: "contracts",
                column: "contractor_id",
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_contracts_deadline",
                schema: "contracts",
                table: "contracts",
                column: "deadline",
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_contracts_issuer_id_id",
                schema: "contracts",
                table: "contracts",
                columns: new[] { "issuer_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contracts",
                schema: "contracts");
        }
    }
}
