using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class EventStatusLogsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_EventStatusLogs_EventId_Timestamp_Id",
                table: "EventStatusLogs",
                columns: new[] { "EventId", "Timestamp", "Id" })
                .Annotation("Npgsql:IndexInclude", new[] { "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventStatusLogs_EventId_Timestamp_Id",
                table: "EventStatusLogs");
        }
    }
}
