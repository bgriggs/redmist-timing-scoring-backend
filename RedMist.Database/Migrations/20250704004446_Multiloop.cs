using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class Multiloop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MultiloopIp",
                table: "Organizations",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MultiloopPort",
                table: "Organizations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OrbitsLogsPath",
                table: "Organizations",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RMonitorIp",
                table: "Organizations",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RMonitorPort",
                table: "Organizations",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MultiloopIp",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "MultiloopPort",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "OrbitsLogsPath",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "RMonitorIp",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "RMonitorPort",
                table: "Organizations");
        }
    }
}
