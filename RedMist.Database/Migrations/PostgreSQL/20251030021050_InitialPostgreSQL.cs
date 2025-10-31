using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RedMist.Database.Migrations.PostgreSQL
{
    /// <inheritdoc />
    public partial class InitialPostgreSQL : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CarLapLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    CarNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LapNumber = table.Column<int>(type: "integer", nullable: false),
                    Flag = table.Column<int>(type: "integer", nullable: false),
                    LapData = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarLapLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CarLastLaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    CarNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastLapNumber = table.Column<int>(type: "integer", nullable: false),
                    LastLapTimestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarLastLaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CompetitorMetadata",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    CarNumber = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Transponder = table.Column<long>(type: "bigint", nullable: false),
                    Transponder2 = table.Column<long>(type: "bigint", nullable: false),
                    Class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NationState = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Sponsor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Make = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Hometown = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Club = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelEngine = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Tires = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Email = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompetitorMetadata", x => new { x.EventId, x.CarNumber });
                });

            migrationBuilder.CreateTable(
                name: "DefaultOrgImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImageData = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DefaultOrgImages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsLive = table.Column<bool>(type: "boolean", nullable: false),
                    EventUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Schedule = table.Column<string>(type: "jsonb", nullable: false),
                    EnableSourceDataLogging = table.Column<bool>(type: "boolean", nullable: false),
                    TrackName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CourseConfiguration = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Distance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Broadcast = table.Column<string>(type: "jsonb", nullable: false),
                    LoopsMetadata = table.Column<string>(type: "jsonb", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventStatusLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Data = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventStatusLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlagLog",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    Flag = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagLog", x => new { x.EventId, x.SessionId, x.Flag, x.StartTime });
                });

            migrationBuilder.CreateTable(
                name: "GoogleSheetsConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Json = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleSheetsConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ShortName = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Website = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Logo = table.Column<byte[]>(type: "bytea", nullable: true),
                    ControlLogType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ControlLogParams = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Orbits = table.Column<string>(type: "jsonb", nullable: false),
                    X2 = table.Column<string>(type: "jsonb", nullable: false),
                    RMonitorIp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RMonitorPort = table.Column<int>(type: "integer", nullable: false),
                    MultiloopIp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    MultiloopPort = table.Column<int>(type: "integer", nullable: false),
                    OrbitsLogsPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RelayLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Exception = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SessionResults",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    SessionState = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionResults", x => new { x.EventId, x.SessionId });
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LocalTimeZoneOffset = table.Column<double>(type: "double precision", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsLive = table.Column<bool>(type: "boolean", nullable: false),
                    IsPracticeQualifying = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => new { x.Id, x.EventId });
                });

            migrationBuilder.CreateTable(
                name: "UIVersions",
                columns: table => new
                {
                    LatestIOSVersion = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    MinimumIOSVersion = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    RecommendIOSUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    IsIOSMinimumMandatory = table.Column<bool>(type: "boolean", nullable: false),
                    LatestAndroidVersion = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    MinimumAndroidVersion = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    RecommendAndroidUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    IsAndroidMinimumMandatory = table.Column<bool>(type: "boolean", nullable: false),
                    LatestWebVersion = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    MinimumWebVersion = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    RecommendWebUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    IsWebMinimumMandatory = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateTable(
                name: "UserOrganizationMappings",
                columns: table => new
                {
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserOrganizationMappings", x => new { x.Username, x.OrganizationId });
                });

            migrationBuilder.CreateTable(
                name: "X2Loops",
                columns: table => new
                {
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Latitude0 = table.Column<double>(type: "double precision", nullable: false),
                    Longitude0 = table.Column<double>(type: "double precision", nullable: false),
                    Latitude1 = table.Column<double>(type: "double precision", nullable: false),
                    Longitude1 = table.Column<double>(type: "double precision", nullable: false),
                    Order = table.Column<long>(type: "bigint", nullable: false),
                    IsInPit = table.Column<bool>(type: "boolean", nullable: false),
                    IsOnline = table.Column<bool>(type: "boolean", nullable: false),
                    HasActivity = table.Column<bool>(type: "boolean", nullable: false),
                    IsSyncOk = table.Column<bool>(type: "boolean", nullable: false),
                    HasDeviceWarnings = table.Column<bool>(type: "boolean", nullable: false),
                    HasDeviceErrors = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_X2Loops", x => new { x.OrganizationId, x.EventId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "X2Passings",
                columns: table => new
                {
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    LoopId = table.Column<long>(type: "bigint", nullable: false),
                    TransponderId = table.Column<long>(type: "bigint", nullable: false),
                    TransponderShortId = table.Column<long>(type: "bigint", nullable: false),
                    Hits = table.Column<int>(type: "integer", nullable: false),
                    TimestampLocal = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsInPit = table.Column<bool>(type: "boolean", nullable: false),
                    IsResend = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_X2Passings", x => new { x.OrganizationId, x.EventId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_ClientId",
                table: "Organizations",
                column: "ClientId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CarLapLogs");

            migrationBuilder.DropTable(
                name: "CarLastLaps");

            migrationBuilder.DropTable(
                name: "CompetitorMetadata");

            migrationBuilder.DropTable(
                name: "DefaultOrgImages");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "EventStatusLogs");

            migrationBuilder.DropTable(
                name: "FlagLog");

            migrationBuilder.DropTable(
                name: "GoogleSheetsConfigs");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropTable(
                name: "RelayLogs");

            migrationBuilder.DropTable(
                name: "SessionResults");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "UIVersions");

            migrationBuilder.DropTable(
                name: "UserOrganizationMappings");

            migrationBuilder.DropTable(
                name: "X2Loops");

            migrationBuilder.DropTable(
                name: "X2Passings");
        }
    }
}
