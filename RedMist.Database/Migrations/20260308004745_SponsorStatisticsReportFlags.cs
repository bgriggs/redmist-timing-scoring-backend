using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class SponsorStatisticsReportFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReportProcessed",
                table: "SponsorStatistics",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReportProcessingSuccessful",
                table: "SponsorStatistics",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReportProcessed",
                table: "SponsorStatistics");

            migrationBuilder.DropColumn(
                name: "ReportProcessingSuccessful",
                table: "SponsorStatistics");
        }
    }
}
