using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddEventViewerSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventViewerSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    ConnectionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StartInferred = table.Column<bool>(type: "boolean", nullable: false),
                    IsInCar = table.Column<bool>(type: "boolean", nullable: false),
                    CarNumber = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    InstallId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventViewerSessions", x => x.Id);
                    table.CheckConstraint("CK_EventViewerSessions_EndReason", "\"EndReason\" IN ('Unsubscribed', 'Disconnected', 'Switched', 'ReconciledAbsent', 'CappedDuration', 'EventTeardown') OR \"EndReason\" IS NULL");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventViewerSessions_EventId_ConnectionId_StartUtc",
                table: "EventViewerSessions",
                columns: new[] { "EventId", "ConnectionId", "StartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventViewerSessions_EventId_StartUtc",
                table: "EventViewerSessions",
                columns: new[] { "EventId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EventViewerSessions_Open",
                table: "EventViewerSessions",
                column: "EventId",
                filter: "\"EndUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventViewerSessions");
        }
    }
}
