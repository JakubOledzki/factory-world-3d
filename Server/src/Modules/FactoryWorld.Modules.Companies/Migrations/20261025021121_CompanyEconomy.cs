using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Companies.Migrations
{
    /// <inheritdoc />
    public partial class CompanyEconomy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "manager_id",
                schema: "companies",
                table: "plots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "cash_cents",
                schema: "companies",
                table: "companies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dividend_at",
                schema: "companies",
                table: "companies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "dividend_percent",
                schema: "companies",
                table: "companies",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<long>(
                name: "income_cents",
                schema: "companies",
                table: "companies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "listed",
                schema: "companies",
                table: "companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "deals",
                schema: "companies",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offered_by = table.Column<Guid>(type: "uuid", nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    per_day = table.Column<long>(type: "bigint", nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false),
                    days = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    settled_days = table.Column<int>(type: "integer", nullable: false),
                    missed_days = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "income",
                schema: "companies",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_income", x => new { x.company_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "companies",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    week = table.Column<int>(type: "integer", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    delivered = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => new { x.company_id, x.week, x.slot });
                });

            migrationBuilder.CreateTable(
                name: "project_parts",
                schema: "companies",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delivered = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_parts", x => new { x.company_id, x.project, x.item });
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "companies",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rewarded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => new { x.company_id, x.project });
                });

            migrationBuilder.CreateTable(
                name: "seasons",
                schema: "companies",
                columns: table => new
                {
                    season = table.Column<int>(type: "integer", nullable: false),
                    place = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    value_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seasons", x => new { x.season, x.place });
                });

            migrationBuilder.CreateTable(
                name: "shares",
                schema: "companies",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shares = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shares", x => new { x.company_id, x.account_id });
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                schema: "companies",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    delivered = table.Column<long>(type: "bigint", nullable: false),
                    wage_cents = table.Column<long>(type: "bigint", nullable: false),
                    paid_cents = table.Column<long>(type: "bigint", nullable: false),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_companies_cash",
                schema: "companies",
                table: "companies",
                sql: "cash_cents >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_deals_buyer_id",
                schema: "companies",
                table: "deals",
                column: "buyer_id");

            migrationBuilder.CreateIndex(
                name: "IX_deals_supplier_id",
                schema: "companies",
                table: "deals",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "IX_income_day",
                schema: "companies",
                table: "income",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "IX_seasons_company_id",
                schema: "companies",
                table: "seasons",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_shares_account_id",
                schema: "companies",
                table: "shares",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_company_id",
                schema: "companies",
                table: "tasks",
                column: "company_id");

            // Companies founded before shares existed: their owners hold the founder's shares.
            migrationBuilder.Sql(
                """
                INSERT INTO companies.shares (company_id, account_id, shares)
                SELECT company_id, account_id, 1000 FROM companies.members WHERE role = 'owner'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deals",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "income",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "project_parts",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "seasons",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "shares",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "tasks",
                schema: "companies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_companies_cash",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "manager_id",
                schema: "companies",
                table: "plots");

            migrationBuilder.DropColumn(
                name: "cash_cents",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "dividend_at",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "dividend_percent",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "income_cents",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "listed",
                schema: "companies",
                table: "companies");
        }
    }
}
