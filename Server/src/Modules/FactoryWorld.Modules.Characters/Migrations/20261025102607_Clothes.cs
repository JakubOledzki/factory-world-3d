using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Characters.Migrations
{
    /// <inheritdoc />
    public partial class Clothes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClothesBody",
                schema: "characters",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClothesFeet",
                schema: "characters",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClothesHead",
                schema: "characters",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClothesLegs",
                schema: "characters",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Looks saved before faces, eye colours and facial hair get the first of each.
            migrationBuilder.Sql(
                """UPDATE characters.characters SET "Appearance" = jsonb_build_object('FaceStyle', 0, 'EyeColor', 0, 'FacialHair', 0) || "Appearance";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClothesBody",
                schema: "characters",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "ClothesFeet",
                schema: "characters",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "ClothesHead",
                schema: "characters",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "ClothesLegs",
                schema: "characters",
                table: "characters");
        }
    }
}
