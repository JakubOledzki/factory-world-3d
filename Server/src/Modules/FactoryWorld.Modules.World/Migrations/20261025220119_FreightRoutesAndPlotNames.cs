using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class FreightRoutesAndPlotNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name",
                schema: "world",
                table: "plots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "freight_routes",
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
                    quantities = table.Column<long[]>(type: "bigint[]", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    next_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    carried_items = table.Column<string[]>(type: "text[]", nullable: false),
                    carried_quantities = table.Column<long[]>(type: "bigint[]", nullable: false),
                    stock = table.Column<long[]>(type: "bigint[]", nullable: false),
                    trips = table.Column<long>(type: "bigint", nullable: false),
                    delivered = table.Column<long>(type: "bigint", nullable: false),
                    last_fee_cents = table.Column<long>(type: "bigint", nullable: false),
                    last_problem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    stopping = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_freight_routes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_freight_routes_next_at",
                schema: "world",
                table: "freight_routes",
                column: "next_at");

            migrationBuilder.CreateIndex(
                name: "IX_freight_routes_owner_id",
                schema: "world",
                table: "freight_routes",
                column: "owner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "freight_routes",
                schema: "world");

            migrationBuilder.DropColumn(
                name: "name",
                schema: "world",
                table: "plots");
        }
    }
}
