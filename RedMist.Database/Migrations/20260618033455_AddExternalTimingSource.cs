using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalTimingSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalConfig",
                table: "Events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimingSource",
                table: "Events",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalConfig",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "TimingSource",
                table: "Events");
        }
    }
}
