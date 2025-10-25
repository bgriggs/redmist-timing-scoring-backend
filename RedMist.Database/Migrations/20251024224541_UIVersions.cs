using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class UIVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UIVersions",
                columns: table => new
                {
                    LatestIOSVersion = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    MinimumIOSVersion = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    RecommendIOSUpdate = table.Column<bool>(type: "bit", nullable: false),
                    IsIOSMinimumMandatory = table.Column<bool>(type: "bit", nullable: false),
                    LatestAndroidVersion = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    MinimumAndroidVersion = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    RecommendAndroidUpdate = table.Column<bool>(type: "bit", nullable: false),
                    IsAndroidMinimumMandatory = table.Column<bool>(type: "bit", nullable: false),
                    LatestWebVersion = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    MinimumWebVersion = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    RecommendWebUpdate = table.Column<bool>(type: "bit", nullable: false),
                    IsWebMinimumMandatory = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UIVersions");
        }
    }
}
