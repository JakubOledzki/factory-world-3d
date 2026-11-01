using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Achievements.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "achievements");

            migrationBuilder.CreateTable(
                name: "badges",
                schema: "achievements",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    badge_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    unlocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_badges", x => new { x.owner_id, x.badge_id });
                });

            migrationBuilder.CreateTable(
                name: "stats",
                schema: "achievements",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stat = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stats", x => new { x.owner_id, x.stat });
                });

            migrationBuilder.CreateIndex(
                name: "IX_stats_stat_value",
                schema: "achievements",
                table: "stats",
                columns: new[] { "stat", "value" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "badges",
                schema: "achievements");

            migrationBuilder.DropTable(
                name: "stats",
                schema: "achievements");
        }
    }
}
