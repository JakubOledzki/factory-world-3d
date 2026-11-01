using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Characters.Migrations
{
    /// <inheritdoc />
    public partial class CharacterMeals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Meal",
                schema: "characters",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MealUntil",
                schema: "characters",
                table: "characters",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Meal",
                schema: "characters",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "MealUntil",
                schema: "characters",
                table: "characters");
        }
    }
}
