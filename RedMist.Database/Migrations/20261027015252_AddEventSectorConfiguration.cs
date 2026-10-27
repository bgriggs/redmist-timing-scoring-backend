using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddEventSectorConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventSectorConfigurations",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSectorConfigurations", x => x.EventId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventSectorConfigurations");
        }
    }
}
