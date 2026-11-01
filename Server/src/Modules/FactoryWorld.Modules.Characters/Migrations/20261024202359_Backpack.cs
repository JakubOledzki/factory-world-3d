using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Characters.Migrations
{
    /// <inheritdoc />
    public partial class Backpack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Backpack",
                schema: "characters",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tool2",
                schema: "characters",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tool3",
                schema: "characters",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Backpack",
                schema: "characters",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "Tool2",
                schema: "characters",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "Tool3",
                schema: "characters",
                table: "characters");
        }
    }
}
