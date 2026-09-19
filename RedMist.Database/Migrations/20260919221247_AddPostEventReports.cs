using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddPostEventReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationReportSettings",
                columns: table => new
                {
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    SendPostEventReport = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationReportSettings", x => x.OrganizationId);
                });

            migrationBuilder.CreateTable(
                name: "PostEventReports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SectionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    SuggestionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    RecipientCount = table.Column<int>(type: "integer", nullable: false),
                    SendFailureCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostEventReports", x => x.Id);
                    table.CheckConstraint("CK_PostEventReports_State", "\"State\" IN ('Pending', 'Sent', 'PartiallySent', 'NoContent', 'Suppressed', 'NoRecipients', 'Failed')");
                });

            migrationBuilder.CreateTable(
                name: "EventViewershipSummaries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PostEventReportId = table.Column<long>(type: "bigint", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TrackOffsetMinutes = table.Column<int>(type: "integer", nullable: true),
                    TotalViewerMinutes = table.Column<double>(type: "double precision", nullable: false),
                    MaxConcurrent = table.Column<int>(type: "integer", nullable: false),
                    PeakUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    TopClientType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SessionCount = table.Column<int>(type: "integer", nullable: false),
                    AnomalousSessions = table.Column<int>(type: "integer", nullable: false),
                    OpenSessions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventViewershipSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventViewershipSummaries_PostEventReports_PostEventReportId",
                        column: x => x.PostEventReportId,
                        principalTable: "PostEventReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventViewershipBuckets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventViewershipSummaryId = table.Column<long>(type: "bigint", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: true),
                    BucketStartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ClientType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MinConcurrent = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrent = table.Column<int>(type: "integer", nullable: false),
                    AvgConcurrent = table.Column<double>(type: "double precision", nullable: false),
                    ViewerSeconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventViewershipBuckets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventViewershipBuckets_EventViewershipSummaries_EventViewer~",
                        column: x => x.EventViewershipSummaryId,
                        principalTable: "EventViewershipSummaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventViewershipSessionSummaries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventViewershipSummaryId = table.Column<long>(type: "bigint", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    SessionName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IsPracticeQualifying = table.Column<bool>(type: "boolean", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TotalViewerMinutes = table.Column<double>(type: "double precision", nullable: false),
                    MaxConcurrent = table.Column<int>(type: "integer", nullable: false),
                    PeakUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    TopClientType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventViewershipSessionSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventViewershipSessionSummaries_EventViewershipSummaries_Ev~",
                        column: x => x.EventViewershipSummaryId,
                        principalTable: "EventViewershipSummaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventViewershipBuckets_EventViewershipSummaryId_SessionId_B~",
                table: "EventViewershipBuckets",
                columns: new[] { "EventViewershipSummaryId", "SessionId", "BucketStartUtc", "ClientType" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_EventViewershipSessionSummaries_EventViewershipSummaryId",
                table: "EventViewershipSessionSummaries",
                column: "EventViewershipSummaryId");

            migrationBuilder.CreateIndex(
                name: "IX_EventViewershipSummaries_PostEventReportId",
                table: "EventViewershipSummaries",
                column: "PostEventReportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostEventReports_EventId",
                table: "PostEventReports",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostEventReports_OrganizationId_GeneratedUtc",
                table: "PostEventReports",
                columns: new[] { "OrganizationId", "GeneratedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventViewershipBuckets");

            migrationBuilder.DropTable(
                name: "EventViewershipSessionSummaries");

            migrationBuilder.DropTable(
                name: "OrganizationReportSettings");

            migrationBuilder.DropTable(
                name: "EventViewershipSummaries");

            migrationBuilder.DropTable(
                name: "PostEventReports");
        }
    }
}
