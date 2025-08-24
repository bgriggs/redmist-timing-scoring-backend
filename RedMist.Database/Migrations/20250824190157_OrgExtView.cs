using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class OrgExtView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
            CREATE VIEW [dbo].[OrganizationExtView] AS
            SELECT 
                o.Id,
                o.ClientId,
                o.ControlLogParams,
                o.ControlLogType,
                COALESCE(o.Logo, d.ImageData) AS Logo,
                o.Name,
                o.Orbits,
                o.ShortName,
                o.Website,
                o.X2
            FROM Organizations o
            CROSS JOIN (SELECT TOP 1 ImageData FROM DefaultOrgImages) d
        ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW [dbo].[OrganizationExtView]");
        }
    }
}
