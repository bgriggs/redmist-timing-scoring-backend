using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class EvtSch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleJson",
                table: "Events");

            migrationBuilder.AddColumn<string>(
                name: "EventUrl",
                table: "Events",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Schedule",
                table: "Events",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventUrl",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Schedule",
                table: "Events");

            migrationBuilder.AddColumn<string>(
                name: "ScheduleJson",
                table: "Events",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");
        }
    }
}
