using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.TimingAndScoringService.Migrations
{
    /// <inheritdoc />
    public partial class EventLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableSourceDataLogging",
                table: "Events",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableSourceDataLogging",
                table: "Events");
        }
    }
}
