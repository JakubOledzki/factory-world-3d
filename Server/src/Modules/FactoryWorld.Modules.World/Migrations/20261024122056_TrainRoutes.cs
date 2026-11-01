using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class TrainRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "train_routes",
                schema: "world",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_x = table.Column<int>(type: "integer", nullable: false),
                    from_y = table.Column<int>(type: "integer", nullable: false),
                    to_x = table.Column<int>(type: "integer", nullable: false),
                    to_y = table.Column<int>(type: "integer", nullable: false),
                    items = table.Column<string[]>(type: "text[]", nullable: false),
                    every_minutes = table.Column<int>(type: "integer", nullable: false),
                    next_departure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_departed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_quantity = table.Column<long>(type: "bigint", nullable: false),
                    last_problem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_train_routes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_train_routes_from_x_from_y",
                schema: "world",
                table: "train_routes",
                columns: new[] { "from_x", "from_y" });

            migrationBuilder.CreateIndex(
                name: "IX_train_routes_next_departure_at",
                schema: "world",
                table: "train_routes",
                column: "next_departure_at");

            migrationBuilder.CreateIndex(
                name: "IX_train_routes_to_x_to_y",
                schema: "world",
                table: "train_routes",
                columns: new[] { "to_x", "to_y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "train_routes",
                schema: "world");
        }
    }
}
