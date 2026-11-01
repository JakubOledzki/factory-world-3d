using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Recycling.Migrations
{
    /// <inheritdoc />
    public partial class FurnitureGrid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Positions in metres do not map onto the new grid: clear the floors and let every
            // garage be furnished again with the starter layout on the grid.
            migrationBuilder.Sql("DELETE FROM recycling.garage_objects; UPDATE recycling.garages SET furnished = FALSE;");

            migrationBuilder.AlterColumn<int>(
                name: "y",
                schema: "recycling",
                table: "garage_objects",
                type: "integer",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AlterColumn<int>(
                name: "x",
                schema: "recycling",
                table: "garage_objects",
                type: "integer",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<int>(
                name: "rotation",
                schema: "recycling",
                table: "garage_objects",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rotation",
                schema: "recycling",
                table: "garage_objects");

            migrationBuilder.AlterColumn<double>(
                name: "y",
                schema: "recycling",
                table: "garage_objects",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<double>(
                name: "x",
                schema: "recycling",
                table: "garage_objects",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");
        }
    }
}
