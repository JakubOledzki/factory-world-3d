using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Recycling.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "recycling");

            migrationBuilder.CreateTable(
                name: "garages",
                schema: "recycling",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_garages", x => x.owner_id);
                });

            migrationBuilder.CreateTable(
                name: "sorting_jobs",
                schema: "recycling",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    finishes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sorting_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sorting_jobs_owner_id_finishes_at",
                schema: "recycling",
                table: "sorting_jobs",
                columns: new[] { "owner_id", "finishes_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "garages",
                schema: "recycling");

            migrationBuilder.DropTable(
                name: "sorting_jobs",
                schema: "recycling");
        }
    }
}
