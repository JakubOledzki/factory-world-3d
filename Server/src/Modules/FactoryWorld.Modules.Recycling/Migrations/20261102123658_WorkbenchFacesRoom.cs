using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Recycling.Migrations
{
    /// <inheritdoc />
    public partial class WorkbenchFacesRoom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Garages made before: the starter workbench stood with its back to the room
            migrationBuilder.Sql(
                "UPDATE recycling.garage_objects SET rotation = 2 WHERE kind = 'furniture_workbench' AND x = 10 AND y = 10 AND rotation = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE recycling.garage_objects SET rotation = 0 WHERE kind = 'furniture_workbench' AND x = 10 AND y = 10 AND rotation = 2;");
        }
    }
}
