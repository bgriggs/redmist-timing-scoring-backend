using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RedMist.Database.Migrations
{
    /// <inheritdoc />
    public partial class FixEventIdAlwaysIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Change IDENTITY BY DEFAULT to IDENTITY ALWAYS for Organizations
            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "Organizations",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            // Change IDENTITY BY DEFAULT to IDENTITY ALWAYS for Events
            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "Events",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            // =========================================================================
            // RESET ALL IDENTITY SEQUENCES
            // =========================================================================
            // This fixes the issue where data migrated from SQL Server or seeded during
            // migrations doesn't automatically update the PostgreSQL sequence.
            // Without this, the next INSERT will try to use ID 1, which may already exist.
            //
            // The COALESCE handles empty tables by starting at 1.
            // The 'true' parameter ensures the next value is MAX(Id) + 1.
            // =========================================================================

            // Reset sequence for Events (may have migrated data)
            migrationBuilder.Sql(@"
 SELECT setval(
     pg_get_serial_sequence('""Events""', 'Id'), 
             COALESCE((SELECT MAX(""Id"") FROM ""Events""), 1), 
   true
        );
  ");

            // Reset sequence for Organizations (may have migrated data)
            migrationBuilder.Sql(@"
   SELECT setval(
         pg_get_serial_sequence('""Organizations""', 'Id'), 
     COALESCE((SELECT MAX(""Id"") FROM ""Organizations""), 1), 
      true
 );
            ");

            // Reset sequence for DefaultOrgImages (has seeded data with Id=1)
            migrationBuilder.Sql(@"
      SELECT setval(
        pg_get_serial_sequence('""DefaultOrgImages""', 'Id'), 
       COALESCE((SELECT MAX(""Id"") FROM ""DefaultOrgImages""), 1), 
          true
     );
        ");

            // Reset sequence for GoogleSheetsConfigs (may have migrated data)
            migrationBuilder.Sql(@"
 SELECT setval(
        pg_get_serial_sequence('""GoogleSheetsConfigs""', 'Id'), 
         COALESCE((SELECT MAX(""Id"") FROM ""GoogleSheetsConfigs""), 1), 
     true
     );
      ");

            // Reset sequence for CarLastLaps (may have migrated data)
            migrationBuilder.Sql(@"
     SELECT setval(
                    pg_get_serial_sequence('""CarLastLaps""', 'Id'), 
 COALESCE((SELECT MAX(""Id"") FROM ""CarLastLaps""), 1), 
 true
          );
            ");

            // Reset sequence for CarLapLogs (may have migrated data, uses bigint)
            migrationBuilder.Sql(@"
         SELECT setval(
          pg_get_serial_sequence('""CarLapLogs""', 'Id'), 
   COALESCE((SELECT MAX(""Id"") FROM ""CarLapLogs""), 1), 
             true
                );
          ");

            // Reset sequence for EventStatusLogs (may have migrated data, uses bigint)
            migrationBuilder.Sql(@"
    SELECT setval(
  pg_get_serial_sequence('""EventStatusLogs""', 'Id'), 
      COALESCE((SELECT MAX(""Id"") FROM ""EventStatusLogs""), 1), 
true
       );
         ");

            // Reset sequence for RelayLogs (may have migrated data, uses bigint)
            migrationBuilder.Sql(@"
                SELECT setval(
  pg_get_serial_sequence('""RelayLogs""', 'Id'), 
   COALESCE((SELECT MAX(""Id"") FROM ""RelayLogs""), 1), 
   true
     );
       ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "Organizations",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "Events",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            // Note: We don't reset sequences in the Down migration
            // because the sequence values should remain valid
        }
    }
}
