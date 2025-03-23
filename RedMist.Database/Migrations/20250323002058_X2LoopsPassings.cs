using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class X2LoopsPassings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "X2Loops",
                columns: table => new
                {
                    OrganizationId = table.Column<int>(type: "int", nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Latitude0 = table.Column<double>(type: "float", nullable: false),
                    Longitude0 = table.Column<double>(type: "float", nullable: false),
                    Latitude1 = table.Column<double>(type: "float", nullable: false),
                    Longitude1 = table.Column<double>(type: "float", nullable: false),
                    Order = table.Column<long>(type: "bigint", nullable: false),
                    IsInPit = table.Column<bool>(type: "bit", nullable: false),
                    IsOnline = table.Column<bool>(type: "bit", nullable: false),
                    HasActivity = table.Column<bool>(type: "bit", nullable: false),
                    IsSyncOk = table.Column<bool>(type: "bit", nullable: false),
                    HasDeviceWarnings = table.Column<bool>(type: "bit", nullable: false),
                    HasDeviceErrors = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_X2Loops", x => new { x.OrganizationId, x.EventId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "X2Passings",
                columns: table => new
                {
                    OrganizationId = table.Column<int>(type: "int", nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    LoopId = table.Column<long>(type: "bigint", nullable: false),
                    TransponderId = table.Column<long>(type: "bigint", nullable: false),
                    TransponderShortId = table.Column<long>(type: "bigint", nullable: false),
                    Hits = table.Column<int>(type: "int", nullable: false),
                    TimestampLocal = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsInPit = table.Column<bool>(type: "bit", nullable: false),
                    IsResend = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_X2Passings", x => new { x.OrganizationId, x.EventId, x.Id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "X2Loops");

            migrationBuilder.DropTable(
                name: "X2Passings");
        }
    }
}
