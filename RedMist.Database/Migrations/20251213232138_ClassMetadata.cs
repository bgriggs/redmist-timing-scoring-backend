using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class ClassMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clear existing data from Orbits column before renaming
            migrationBuilder.Sql("UPDATE \"Organizations\" SET \"Orbits\" = '[]';");

            migrationBuilder.RenameColumn(
                name: "Orbits",
                table: "Organizations",
                newName: "Classes");

            migrationBuilder.AddColumn<bool>(
                name: "ShowControlLogConnection",
                table: "Organizations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowFlagtronicsConnection",
                table: "Organizations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowMultiloopConnection",
                table: "Organizations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowOrbitsLogsConnection",
                table: "Organizations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowX2Connection",
                table: "Organizations",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowControlLogConnection",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ShowFlagtronicsConnection",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ShowMultiloopConnection",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ShowOrbitsLogsConnection",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ShowX2Connection",
                table: "Organizations");

            migrationBuilder.RenameColumn(
                name: "Classes",
                table: "Organizations",
                newName: "Orbits");
        }
    }
}
