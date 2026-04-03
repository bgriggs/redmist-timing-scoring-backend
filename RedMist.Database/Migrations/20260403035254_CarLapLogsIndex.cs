using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class CarLapLogsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CarLapLogs_EventId_SessionId_CarNumber_LapNumber",
                table: "CarLapLogs",
                columns: new[] { "EventId", "SessionId", "CarNumber", "LapNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CarLapLogs_EventId_SessionId_CarNumber_LapNumber",
                table: "CarLapLogs");
        }
    }
}
