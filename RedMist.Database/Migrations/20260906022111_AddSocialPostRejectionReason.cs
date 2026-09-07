using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialPostRejectionReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "SocialPosts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "SocialPosts");
        }
    }
}
