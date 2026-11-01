using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Puzzles.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "puzzles");

            migrationBuilder.CreateTable(
                name: "results",
                schema: "puzzles",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    puzzle_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    medal = table.Column<int>(type: "integer", nullable: false),
                    objects = table.Column<int>(type: "integer", nullable: false),
                    rate = table.Column<double>(type: "double precision", nullable: false),
                    layout = table.Column<string>(type: "jsonb", nullable: false),
                    solved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_results", x => new { x.owner_id, x.puzzle_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "results",
                schema: "puzzles");
        }
    }
}
