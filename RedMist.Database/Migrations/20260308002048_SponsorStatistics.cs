using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class SponsorStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SponsorStatistics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    SponsorId = table.Column<int>(type: "integer", nullable: false),
                    ViewableImpressions = table.Column<int>(type: "integer", nullable: false),
                    Impressions = table.Column<int>(type: "integer", nullable: false),
                    EngagementDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ClickThroughs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorStatistics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventSponsorStatistics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SponsorStatisticsId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SponsorId = table.Column<int>(type: "integer", nullable: false),
                    ViewableImpressions = table.Column<int>(type: "integer", nullable: false),
                    Impressions = table.Column<int>(type: "integer", nullable: false),
                    EngagementDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ClickThroughs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSponsorStatistics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSponsorStatistics_SponsorStatistics_SponsorStatisticsId",
                        column: x => x.SponsorStatisticsId,
                        principalTable: "SponsorStatistics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceSponsorStatistics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SponsorStatisticsId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SponsorId = table.Column<int>(type: "integer", nullable: false),
                    ViewableImpressions = table.Column<int>(type: "integer", nullable: false),
                    Impressions = table.Column<int>(type: "integer", nullable: false),
                    EngagementDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ClickThroughs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSponsorStatistics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceSponsorStatistics_SponsorStatistics_SponsorStatistics~",
                        column: x => x.SponsorStatisticsId,
                        principalTable: "SponsorStatistics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventSponsorStatistics_SponsorStatisticsId",
                table: "EventSponsorStatistics",
                column: "SponsorStatisticsId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceSponsorStatistics_SponsorStatisticsId",
                table: "SourceSponsorStatistics",
                column: "SponsorStatisticsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventSponsorStatistics");

            migrationBuilder.DropTable(
                name: "SourceSponsorStatistics");

            migrationBuilder.DropTable(
                name: "SponsorStatistics");
        }
    }
}
