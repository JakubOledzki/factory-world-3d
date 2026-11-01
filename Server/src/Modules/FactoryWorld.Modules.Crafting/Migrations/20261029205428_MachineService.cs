using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FactoryWorld.Modules.Crafting.Migrations
{
    /// <inheritdoc />
    public partial class MachineService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "service_due",
                schema: "crafting",
                table: "machines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "machine_wear",
                schema: "crafting",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    service_due = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machine_wear", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_machine_wear_owner_id_kind",
                schema: "crafting",
                table: "machine_wear",
                columns: new[] { "owner_id", "kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "machine_wear",
                schema: "crafting");

            migrationBuilder.DropColumn(
                name: "service_due",
                schema: "crafting",
                table: "machines");
        }
    }
}
