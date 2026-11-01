using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class HusbandryFarmsteads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "farmstead_id",
                schema: "world",
                table: "plot_fields",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "meals",
                schema: "world",
                table: "plot_animals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "plot_farmsteads",
                schema: "world",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crop = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    store = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    manure = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    busy_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plot_farmsteads", x => x.object_id);
                });

            migrationBuilder.CreateTable(
                name: "town_festivals",
                schema: "world",
                columns: table => new
                {
                    town_x = table.Column<int>(type: "integer", nullable: false),
                    town_y = table.Column<int>(type: "integer", nullable: false),
                    until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_town_festivals", x => new { x.town_x, x.town_y });
                });

            migrationBuilder.CreateIndex(
                name: "IX_plot_fields_farmstead_id",
                schema: "world",
                table: "plot_fields",
                column: "farmstead_id");

            migrationBuilder.CreateIndex(
                name: "IX_plot_farmsteads_owner_id",
                schema: "world",
                table: "plot_farmsteads",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_plot_farmsteads_x_y",
                schema: "world",
                table: "plot_farmsteads",
                columns: new[] { "x", "y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plot_farmsteads",
                schema: "world");

            migrationBuilder.DropTable(
                name: "town_festivals",
                schema: "world");

            migrationBuilder.DropIndex(
                name: "IX_plot_fields_farmstead_id",
                schema: "world",
                table: "plot_fields");

            migrationBuilder.DropColumn(
                name: "farmstead_id",
                schema: "world",
                table: "plot_fields");

            migrationBuilder.DropColumn(
                name: "meals",
                schema: "world",
                table: "plot_animals");
        }
    }
}
