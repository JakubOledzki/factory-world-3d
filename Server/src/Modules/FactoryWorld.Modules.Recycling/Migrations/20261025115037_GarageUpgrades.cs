using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Recycling.Migrations
{
    /// <inheritdoc />
    public partial class GarageUpgrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "garage_items",
                schema: "recycling",
                columns: table => new
                {
                    holder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_garage_items", x => new { x.holder_id, x.item });
                });

            migrationBuilder.CreateTable(
                name: "garage_upgrades",
                schema: "recycling",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    track = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_garage_upgrades", x => new { x.owner_id, x.track });
                });

            migrationBuilder.CreateTable(
                name: "outfits",
                schema: "recycling",
                columns: table => new
                {
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    head = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    body = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    legs = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    feet = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outfits", x => new { x.owner_id, x.number });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "garage_items",
                schema: "recycling");

            migrationBuilder.DropTable(
                name: "garage_upgrades",
                schema: "recycling");

            migrationBuilder.DropTable(
                name: "outfits",
                schema: "recycling");
        }
    }
}
