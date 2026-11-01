using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Mentoring.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mentoring");

            migrationBuilder.CreateTable(
                name: "mentorships",
                schema: "mentoring",
                columns: table => new
                {
                    mentee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mentor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    mentee_rewarded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    mentor_rewarded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mentorships", x => x.mentee_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mentorships_mentor_id",
                schema: "mentoring",
                table: "mentorships",
                column: "mentor_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mentorships",
                schema: "mentoring");
        }
    }
}
