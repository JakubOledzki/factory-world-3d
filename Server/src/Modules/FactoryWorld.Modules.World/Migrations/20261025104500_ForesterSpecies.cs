using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class ForesterSpecies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "oak_wood",
                schema: "world",
                table: "plot_foresters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "species",
                schema: "world",
                table: "plot_foresters",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "pine");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "oak_wood",
                schema: "world",
                table: "plot_foresters");

            migrationBuilder.DropColumn(
                name: "species",
                schema: "world",
                table: "plot_foresters");
        }
    }
}
