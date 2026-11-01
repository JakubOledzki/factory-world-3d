using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class Banks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_accounts",
                schema: "world",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance_cents = table.Column<long>(type: "bigint", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_accounts", x => new { x.company_id, x.player_id });
                });

            migrationBuilder.CreateTable(
                name: "bank_buildings",
                schema: "world",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_buildings", x => x.object_id);
                });

            migrationBuilder.CreateTable(
                name: "bank_transfers",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cents = table.Column<long>(type: "bigint", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    arrives_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_transfers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "banks",
                schema: "world",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_banks", x => x.company_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_player_id",
                schema: "world",
                table: "bank_accounts",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_buildings_company_id",
                schema: "world",
                table: "bank_buildings",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transfers_arrives_at",
                schema: "world",
                table: "bank_transfers",
                column: "arrives_at");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transfers_player_id",
                schema: "world",
                table: "bank_transfers",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transfers_to_player_id_to_company_id",
                schema: "world",
                table: "bank_transfers",
                columns: new[] { "to_player_id", "to_company_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_accounts",
                schema: "world");

            migrationBuilder.DropTable(
                name: "bank_buildings",
                schema: "world");

            migrationBuilder.DropTable(
                name: "bank_transfers",
                schema: "world");

            migrationBuilder.DropTable(
                name: "banks",
                schema: "world");
        }
    }
}
