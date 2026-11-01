using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Law.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "law");

            migrationBuilder.CreateTable(
                name: "action_counts",
                schema: "law",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_counts", x => new { x.player_id, x.day, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "law",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.player_id);
                });

            migrationBuilder.CreateTable(
                name: "cases",
                schema: "law",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    suspect_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    detail = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    value_cents = table.Column<long>(type: "bigint", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fine_cents = table.Column<long>(type: "bigint", nullable: false),
                    collected_cents = table.Column<long>(type: "bigint", nullable: false),
                    trade_ban_hours = table.Column<int>(type: "integer", nullable: false),
                    travel_ban_hours = table.Column<int>(type: "integer", nullable: false),
                    wanted_hours = table.Column<int>(type: "integer", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    appealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    judged_by = table.Column<Guid>(type: "uuid", nullable: true),
                    bonus_paid = table.Column<bool>(type: "boolean", nullable: false),
                    seize_x = table.Column<int>(type: "integer", nullable: true),
                    seize_y = table.Column<int>(type: "integer", nullable: true),
                    seize_items = table.Column<string[]>(type: "text[]", nullable: false),
                    seize_quantities = table.Column<long[]>(type: "bigint[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cases", x => x.id);
                    table.CheckConstraint("ck_cases_fine", "fine_cents >= 0 AND collected_cents >= 0 AND collected_cents <= fine_cents");
                    table.CheckConstraint("ck_cases_hours", "trade_ban_hours >= 0 AND travel_ban_hours >= 0 AND wanted_hours >= 0");
                });

            migrationBuilder.CreateTable(
                name: "compartments",
                schema: "law",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bought_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compartments", x => x.player_id);
                });

            migrationBuilder.CreateTable(
                name: "country_laws",
                schema: "law",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    forest_protection = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_country_laws", x => x.country);
                });

            migrationBuilder.CreateTable(
                name: "country_rules",
                schema: "law",
                columns: table => new
                {
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fee_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_country_rules", x => new { x.country, x.item });
                    table.CheckConstraint("ck_country_rules_fee", "fee_cents >= 0");
                });

            migrationBuilder.CreateTable(
                name: "crossings",
                schema: "law",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    to_country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    from_x = table.Column<int>(type: "integer", nullable: false),
                    from_y = table.Column<int>(type: "integer", nullable: false),
                    to_x = table.Column<int>(type: "integer", nullable: false),
                    to_y = table.Column<int>(type: "integer", nullable: false),
                    items = table.Column<string[]>(type: "text[]", nullable: false),
                    quantities = table.Column<long[]>(type: "bigint[]", nullable: false),
                    declared = table.Column<bool>(type: "boolean", nullable: false),
                    hidden = table.Column<bool>(type: "boolean", nullable: false),
                    duty_cents = table.Column<long>(type: "bigint", nullable: false),
                    evaded_cents = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    inspected_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crossings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "licences",
                schema: "law",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licences", x => new { x.player_id, x.country, x.item });
                });

            migrationBuilder.CreateTable(
                name: "officers",
                schema: "law",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rank = table.Column<short>(type: "smallint", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_officers", x => x.player_id);
                    table.CheckConstraint("ck_officers_rank", "rank BETWEEN 0 AND 3");
                });

            migrationBuilder.CreateTable(
                name: "reports",
                schema: "law",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    suspect_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sanctions",
                schema: "law",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    case_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sanctions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "traces",
                schema: "law",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    suspect_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accomplice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value_cents = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    found = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_traces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "warrants",
                schema: "law",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warrants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_applications_country",
                schema: "law",
                table: "applications",
                column: "country");

            migrationBuilder.CreateIndex(
                name: "IX_cases_country_status",
                schema: "law",
                table: "cases",
                columns: new[] { "country", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_cases_decided_by_bonus_paid",
                schema: "law",
                table: "cases",
                columns: new[] { "decided_by", "bonus_paid" });

            migrationBuilder.CreateIndex(
                name: "IX_cases_suspect_id",
                schema: "law",
                table: "cases",
                column: "suspect_id");

            migrationBuilder.CreateIndex(
                name: "IX_crossings_owner_id_at",
                schema: "law",
                table: "crossings",
                columns: new[] { "owner_id", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_crossings_to_country_at",
                schema: "law",
                table: "crossings",
                columns: new[] { "to_country", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_officers_country",
                schema: "law",
                table: "officers",
                column: "country");

            migrationBuilder.CreateIndex(
                name: "IX_reports_country_closed",
                schema: "law",
                table: "reports",
                columns: new[] { "country", "closed" });

            migrationBuilder.CreateIndex(
                name: "IX_reports_reporter_id_at",
                schema: "law",
                table: "reports",
                columns: new[] { "reporter_id", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_case_id",
                schema: "law",
                table: "sanctions",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_player_id_kind_until",
                schema: "law",
                table: "sanctions",
                columns: new[] { "player_id", "kind", "until" });

            migrationBuilder.CreateIndex(
                name: "IX_traces_country_suspect_id_found",
                schema: "law",
                table: "traces",
                columns: new[] { "country", "suspect_id", "found" });

            migrationBuilder.CreateIndex(
                name: "IX_warrants_country_used",
                schema: "law",
                table: "warrants",
                columns: new[] { "country", "used" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_counts",
                schema: "law");

            migrationBuilder.DropTable(
                name: "applications",
                schema: "law");

            migrationBuilder.DropTable(
                name: "cases",
                schema: "law");

            migrationBuilder.DropTable(
                name: "compartments",
                schema: "law");

            migrationBuilder.DropTable(
                name: "country_laws",
                schema: "law");

            migrationBuilder.DropTable(
                name: "country_rules",
                schema: "law");

            migrationBuilder.DropTable(
                name: "crossings",
                schema: "law");

            migrationBuilder.DropTable(
                name: "licences",
                schema: "law");

            migrationBuilder.DropTable(
                name: "officers",
                schema: "law");

            migrationBuilder.DropTable(
                name: "reports",
                schema: "law");

            migrationBuilder.DropTable(
                name: "sanctions",
                schema: "law");

            migrationBuilder.DropTable(
                name: "traces",
                schema: "law");

            migrationBuilder.DropTable(
                name: "warrants",
                schema: "law");
        }
    }
}
