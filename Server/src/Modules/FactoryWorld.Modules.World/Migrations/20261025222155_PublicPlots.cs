using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.World.Migrations
{
    /// <inheritdoc />
    public partial class PublicPlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "public",
                schema: "world",
                table: "plots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The bots' plots are open to everyone; the bots open the ones they buy later themselves.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('bots.bots') IS NOT NULL THEN
                        UPDATE world.plots SET public = true WHERE owner_id IN (SELECT account_id FROM bots.bots);
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "public",
                schema: "world",
                table: "plots");
        }
    }
}
