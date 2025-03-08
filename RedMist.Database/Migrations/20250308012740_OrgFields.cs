using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class OrgFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Logo",
                table: "Organizations",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                table: "Organizations",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Logo",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "Website",
                table: "Organizations");
        }
    }
}
