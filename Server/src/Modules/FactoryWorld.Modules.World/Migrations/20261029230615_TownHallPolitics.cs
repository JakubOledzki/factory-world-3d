using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class TownHallPolitics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "recalled",
                schema: "world",
                table: "town_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "town_candidates",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_percent = table.Column<int>(type: "integer", nullable: false),
                    bonus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    slogan = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    deposit_cents = table.Column<long>(type: "bigint", nullable: false),
                    settled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_candidates", x => new { x.town_x, x.town_y, x.week, x.player_id });
                });

            migrationBuilder.CreateTable(
                name: "town_investments",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_investments", x => new { x.town_x, x.town_y, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "town_ledger",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    balance_after_cents = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_ledger", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "town_ordinance_votes",
                schema: "world",
                columns: table => new
                {
                    ordinance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    yes = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_ordinance_votes", x => new { x.ordinance_id, x.voter_id });
                });

            migrationBuilder.CreateTable(
                name: "town_ordinances",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    proposed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    voting_ends = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_ordinances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "town_recalls",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    signer_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_recalls", x => new { x.town_x, x.town_y, x.week, x.signer_id });
                });

            migrationBuilder.CreateTable(
                name: "town_sponsorships",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cents = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_sponsorships", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "town_treasuries",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_treasuries", x => new { x.town_x, x.town_y });
                    table.CheckConstraint("ck_town_treasuries_cents", "cents >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_town_candidates_player_id",
                schema: "world",
                table: "town_candidates",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "IX_town_ledger_town_x_town_y_id",
                schema: "world",
                table: "town_ledger",
                columns: new[] { "town_x", "town_y", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_town_ordinances_town_x_town_y_week_kind",
                schema: "world",
                table: "town_ordinances",
                columns: new[] { "town_x", "town_y", "week", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_town_sponsorships_town_x_town_y_week",
                schema: "world",
                table: "town_sponsorships",
                columns: new[] { "town_x", "town_y", "week" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "town_candidates",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_investments",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_ledger",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_ordinance_votes",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_ordinances",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_recalls",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_sponsorships",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_treasuries",
                schema: "world");

            migrationBuilder.DropColumn(
                name: "recalled",
                schema: "world",
                table: "town_policies");
        }
    }
}
