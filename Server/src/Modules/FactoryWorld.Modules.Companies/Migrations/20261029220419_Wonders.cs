using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Companies.Migrations
{
    /// <inheritdoc />
    public partial class Wonders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wonders",
                schema: "companies",
                columns: table => new
                {
                    project = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wonders", x => x.project);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wonders",
                schema: "companies");
        }
    }
}
