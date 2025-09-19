using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RedMist.Migrations
{
    /// <inheritdoc />
    public partial class MultiloopOrgView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW [dbo].[OrganizationExtView]");
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
                o.X2,
                o.MultiloopIp,
                o.MultiloopPort,
                o.OrbitsLogsPath,
                o.RMonitorIp,
                o.RMonitorPort
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
