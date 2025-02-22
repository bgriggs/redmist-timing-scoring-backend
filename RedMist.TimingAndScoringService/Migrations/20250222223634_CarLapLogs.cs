using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.TimingAndScoringService.Migrations
{
    /// <inheritdoc />
    public partial class CarLapLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CarLapLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CarNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LapNumber = table.Column<int>(type: "int", nullable: false),
                    Flag = table.Column<int>(type: "int", nullable: false),
                    LapData = table.Column<string>(type: "nvarchar(max)", maxLength: 5000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarLapLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CarLapLogs");
        }
    }
}
