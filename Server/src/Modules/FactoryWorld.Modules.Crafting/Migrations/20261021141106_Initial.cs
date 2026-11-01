using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Crafting.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "crafting");

            migrationBuilder.CreateTable(
                name: "furnaces",
                schema: "crafting",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_item = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    input_quantity = table.Column<int>(type: "integer", nullable: false),
                    fuel = table.Column<int>(type: "integer", nullable: false),
                    heat = table.Column<int>(type: "integer", nullable: false),
                    output_item = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    output_quantity = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_furnaces", x => x.object_id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "crafting",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finishes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_owner_id_finishes_at",
                schema: "crafting",
                table: "jobs",
                columns: new[] { "owner_id", "finishes_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "furnaces",
                schema: "crafting");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "crafting");
        }
    }
}
