using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryWorld.Modules.Power.Migrations
{
    /// <inheritdoc />
    public partial class PowerLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lines",
                schema: "power",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_x = table.Column<int>(type: "integer", nullable: false),
                    from_y = table.Column<int>(type: "integer", nullable: false),
                    from_tie = table.Column<Guid>(type: "uuid", nullable: false),
                    to_x = table.Column<int>(type: "integer", nullable: false),
                    to_y = table.Column<int>(type: "integer", nullable: false),
                    to_tie = table.Column<Guid>(type: "uuid", nullable: false),
                    kw = table.Column<double>(type: "double precision", nullable: false),
                    paid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lines", x => x.id);
                    table.CheckConstraint("ck_lines_kw", "kw > 0");
                });

            migrationBuilder.CreateTable(
                name: "ties",
                schema: "power",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false),
                    open = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ties", x => x.id);
                    table.CheckConstraint("ck_ties_price", "price_cents >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_lines_from_tie_to_tie",
                schema: "power",
                table: "lines",
                columns: new[] { "from_tie", "to_tie" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lines_from_x_from_y",
                schema: "power",
                table: "lines",
                columns: new[] { "from_x", "from_y" });

            migrationBuilder.CreateIndex(
                name: "IX_lines_to_x_to_y",
                schema: "power",
                table: "lines",
                columns: new[] { "to_x", "to_y" });

            migrationBuilder.CreateIndex(
                name: "IX_ties_x_y",
                schema: "power",
                table: "ties",
                columns: new[] { "x", "y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lines",
                schema: "power");

            migrationBuilder.DropTable(
                name: "ties",
                schema: "power");
        }
    }
}
