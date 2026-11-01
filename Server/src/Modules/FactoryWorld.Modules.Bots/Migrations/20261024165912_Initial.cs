using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Bots.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "bots");

            migrationBuilder.CreateTable(
                name: "bots",
                schema: "bots",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    character_name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_session_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bots", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bots_account_id",
                schema: "bots",
                table: "bots",
                column: "account_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bots",
                schema: "bots");
        }
    }
}
