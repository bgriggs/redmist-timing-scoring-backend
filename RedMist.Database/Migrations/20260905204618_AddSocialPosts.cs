using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SocialPosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: true),
                    Channel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ScheduledUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApprovedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DigestJson = table.Column<string>(type: "jsonb", nullable: true),
                    DigestSourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GeneratedText = table.Column<string>(type: "text", nullable: true),
                    EditedText = table.Column<string>(type: "text", nullable: true),
                    ImageRefs = table.Column<string>(type: "jsonb", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PromptVersion = table.Column<int>(type: "integer", nullable: true),
                    DigestVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    GenerationAttempts = table.Column<int>(type: "integer", nullable: false),
                    ValidationWarnings = table.Column<string>(type: "text", nullable: true),
                    ExternalPostId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExternalUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    PublishAttempts = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPosts", x => x.Id);
                    table.CheckConstraint("CK_SocialPosts_Channel", "\"Channel\" IN ('Facebook', 'Instagram')");
                    table.CheckConstraint("CK_SocialPosts_Kind", "\"Kind\" IN ('EventResults', 'FeatureAnnouncement', 'Manual')");
                    table.CheckConstraint("CK_SocialPosts_State", "\"State\" IN ('Draft', 'PendingReview', 'Approved', 'Publishing', 'Published', 'Rejected', 'Failed')");
                });

            migrationBuilder.CreateTable(
                name: "SocialPrompts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Channel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    VoiceGuide = table.Column<string>(type: "text", nullable: false),
                    FewShotJson = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPrompts", x => x.Id);
                    table.CheckConstraint("CK_SocialPrompts_Channel", "\"Channel\" IN ('Facebook', 'Instagram')");
                    table.CheckConstraint("CK_SocialPrompts_Kind", "\"Kind\" IN ('EventResults', 'FeatureAnnouncement', 'Manual')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_IdempotencyKey",
                table: "SocialPosts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_State_ScheduledUtc",
                table: "SocialPosts",
                columns: new[] { "State", "ScheduledUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPrompts_Kind_Channel",
                table: "SocialPrompts",
                columns: new[] { "Kind", "Channel" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_SocialPrompts_Kind_Channel_Version",
                table: "SocialPrompts",
                columns: new[] { "Kind", "Channel", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SocialPosts");

            migrationBuilder.DropTable(
                name: "SocialPrompts");
        }
    }
}
