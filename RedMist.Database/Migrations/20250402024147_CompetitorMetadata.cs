using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class CompetitorMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompetitorMetadata",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CarNumber = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Transponder = table.Column<long>(type: "bigint", nullable: false),
                    Transponder2 = table.Column<long>(type: "bigint", nullable: false),
                    Class = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NationState = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Sponsor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Make = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Hometown = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Club = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ModelEngine = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Tires = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompetitorMetadata", x => new { x.EventId, x.CarNumber });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompetitorMetadata");
        }
    }
}
