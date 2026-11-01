using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Politics.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "politics");

            migrationBuilder.CreateTable(
                name: "alliance_offers",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    other = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    offered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alliance_offers", x => new { x.country, x.other });
                });

            migrationBuilder.CreateTable(
                name: "candidates",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    term = table.Column<int>(type: "integer", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plot_tax_per_mille = table.Column<int>(type: "integer", nullable: false),
                    foreigner_fee_percent = table.Column<int>(type: "integer", nullable: false),
                    slogan = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    deposit_cents = table.Column<long>(type: "bigint", nullable: false),
                    settled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidates", x => new { x.country, x.term, x.player_id });
                });

            migrationBuilder.CreateTable(
                name: "citizens",
                schema: "politics",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_citizens", x => x.player_id);
                });

            migrationBuilder.CreateTable(
                name: "governments",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    term = table.Column<int>(type: "integer", nullable: false),
                    president_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plot_tax_per_mille = table.Column<int>(type: "integer", nullable: false),
                    foreigner_fee_percent = table.Column<int>(type: "integer", nullable: false),
                    subsidy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    tariff_percent = table.Column<int>(type: "integer", nullable: false),
                    recalled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_governments", x => new { x.country, x.term });
                });

            migrationBuilder.CreateTable(
                name: "investments",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_investments", x => new { x.country, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "item_tariffs",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    percent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_tariffs", x => new { x.country, x.item });
                });

            migrationBuilder.CreateTable(
                name: "ministers",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    term = table.Column<int>(type: "integer", nullable: false),
                    office = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ministers", x => new { x.country, x.term, x.office });
                });

            migrationBuilder.CreateTable(
                name: "parties",
                schema: "politics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    leader_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parties", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "party_members",
                schema: "politics",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_members", x => x.player_id);
                });

            migrationBuilder.CreateTable(
                name: "recalls",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    term = table.Column<int>(type: "integer", nullable: false),
                    signer_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recalls", x => new { x.country, x.term, x.signer_id });
                });

            migrationBuilder.CreateTable(
                name: "relations",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    other = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relations", x => new { x.country, x.other, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "sponsorships",
                schema: "politics",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    term = table.Column<int>(type: "integer", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cents = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sponsorships", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "treasury_ledger",
                schema: "politics",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    balance_after_cents = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_treasury_ledger", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "votes",
                schema: "politics",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    term = table.Column<int>(type: "integer", nullable: false),
                    voter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_votes", x => new { x.country, x.term, x.voter_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_alliance_offers_other",
                schema: "politics",
                table: "alliance_offers",
                column: "other");

            migrationBuilder.CreateIndex(
                name: "IX_candidates_player_id",
                schema: "politics",
                table: "candidates",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "IX_citizens_country",
                schema: "politics",
                table: "citizens",
                column: "country");

            migrationBuilder.CreateIndex(
                name: "IX_governments_president_id",
                schema: "politics",
                table: "governments",
                column: "president_id");

            migrationBuilder.CreateIndex(
                name: "IX_parties_name",
                schema: "politics",
                table: "parties",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_party_members_party_id",
                schema: "politics",
                table: "party_members",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "IX_relations_other",
                schema: "politics",
                table: "relations",
                column: "other");

            migrationBuilder.CreateIndex(
                name: "IX_sponsorships_country_term",
                schema: "politics",
                table: "sponsorships",
                columns: new[] { "country", "term" });

            migrationBuilder.CreateIndex(
                name: "IX_treasury_ledger_country_id",
                schema: "politics",
                table: "treasury_ledger",
                columns: new[] { "country", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alliance_offers",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "candidates",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "citizens",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "governments",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "investments",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "item_tariffs",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "ministers",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "parties",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "party_members",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "recalls",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "relations",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "sponsorships",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "treasury_ledger",
                schema: "politics");

            migrationBuilder.DropTable(
                name: "votes",
                schema: "politics");
        }
    }
}
