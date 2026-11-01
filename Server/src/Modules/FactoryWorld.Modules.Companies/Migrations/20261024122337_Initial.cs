using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Companies.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "companies");

            migrationBuilder.CreateTable(
                name: "companies",
                schema: "companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invites",
                schema: "companies",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invites", x => new { x.company_id, x.account_id });
                });

            migrationBuilder.CreateTable(
                name: "log",
                schema: "companies",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    other = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "members",
                schema: "companies",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_members", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "plots",
                schema: "companies",
                columns: table => new
                {
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plots", x => new { x.x, x.y });
                });

            migrationBuilder.CreateTable(
                name: "warehouse",
                schema: "companies",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse", x => new { x.company_id, x.item });
                    table.CheckConstraint("ck_warehouse_quantity", "quantity >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_companies_normalized_name",
                schema: "companies",
                table: "companies",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invites_account_id",
                schema: "companies",
                table: "invites",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_log_company_id_id",
                schema: "companies",
                table: "log",
                columns: new[] { "company_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_members_company_id",
                schema: "companies",
                table: "members",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_plots_company_id",
                schema: "companies",
                table: "plots",
                column: "company_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "companies",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "invites",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "log",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "members",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "plots",
                schema: "companies");

            migrationBuilder.DropTable(
                name: "warehouse",
                schema: "companies");
        }
    }
}
