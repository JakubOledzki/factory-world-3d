using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class InventoryStackTiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int[]>(
                name: "tiles",
                schema: "inventory",
                table: "stacks",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            // An item keeps the tile it lay on as the tile of its first stack; its other stacks get free tiles when the
            // inventory is listed.
            migrationBuilder.Sql("UPDATE inventory.stacks SET tiles = ARRAY[slot] WHERE slot IS NOT NULL AND quantity > 0");

            migrationBuilder.DropColumn(
                name: "slot",
                schema: "inventory",
                table: "stacks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "slot",
                schema: "inventory",
                table: "stacks",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("UPDATE inventory.stacks SET slot = tiles[1] WHERE cardinality(tiles) > 0");

            migrationBuilder.DropColumn(
                name: "tiles",
                schema: "inventory",
                table: "stacks");
        }
    }
}
