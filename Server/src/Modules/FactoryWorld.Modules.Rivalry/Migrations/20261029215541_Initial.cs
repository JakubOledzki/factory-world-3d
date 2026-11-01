using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Rivalry.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rivalry");

            migrationBuilder.CreateTable(
                name: "challenge_scores",
                schema: "rivalry",
                columns: table => new
                {
                    week = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_challenge_scores", x => new { x.week, x.owner_id });
                    table.CheckConstraint("ck_challenge_scores_value", "value >= 0");
                });

            migrationBuilder.CreateTable(
                name: "challenge_winners",
                schema: "rivalry",
                columns: table => new
                {
                    week = table.Column<int>(type: "integer", nullable: false),
                    place = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    prize_cents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_challenge_winners", x => new { x.week, x.place });
                });

            migrationBuilder.CreateTable(
                name: "firsts",
                schema: "rivalry",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_firsts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settled_weeks",
                schema: "rivalry",
                columns: table => new
                {
                    week = table.Column<int>(type: "integer", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settled_weeks", x => x.week);
                });

            migrationBuilder.CreateIndex(
                name: "IX_challenge_scores_week_value",
                schema: "rivalry",
                table: "challenge_scores",
                columns: new[] { "week", "value" });

            migrationBuilder.CreateIndex(
                name: "IX_challenge_winners_owner_id",
                schema: "rivalry",
                table: "challenge_winners",
                column: "owner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "challenge_scores",
                schema: "rivalry");

            migrationBuilder.DropTable(
                name: "challenge_winners",
                schema: "rivalry");

            migrationBuilder.DropTable(
                name: "firsts",
                schema: "rivalry");

            migrationBuilder.DropTable(
                name: "settled_weeks",
                schema: "rivalry");
        }
    }
}
