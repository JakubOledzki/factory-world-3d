using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Recycling.Migrations
{
    /// <inheritdoc />
    public partial class Furniture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "furnished",
                schema: "recycling",
                table: "garages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "garage_objects",
                schema: "recycling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_garage_objects", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_garage_objects_owner_id",
                schema: "recycling",
                table: "garage_objects",
                column: "owner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "garage_objects",
                schema: "recycling");

            migrationBuilder.DropColumn(
                name: "furnished",
                schema: "recycling",
                table: "garages");
        }
    }
}
