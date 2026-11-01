using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Crafting.Migrations
{
    /// <inheritdoc />
    public partial class ProcessingMachines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "machines",
                schema: "crafting",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recipe = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    inputs = table.Column<int[]>(type: "integer[]", nullable: false),
                    fuel_item = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    fuel = table.Column<int>(type: "integer", nullable: false),
                    heat = table.Column<int>(type: "integer", nullable: false),
                    output = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machines", x => x.object_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "machines",
                schema: "crafting");
        }
    }
}
