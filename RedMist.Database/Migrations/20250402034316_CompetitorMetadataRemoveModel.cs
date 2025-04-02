using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class CompetitorMetadataRemoveModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Model",
                table: "CompetitorMetadata");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "CompetitorMetadata",
                type: "nvarchar(48)",
                maxLength: 48,
                nullable: false,
                defaultValue: "");
        }
    }
}
