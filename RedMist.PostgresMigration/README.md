# RedMist PostgreSQL Migration Tool

This tool migrates data from SQL Server to PostgreSQL for the RedMist Timing and Scoring system.

## Prerequisites

1. **PostgreSQL Database**: Ensure PostgreSQL is installed and running
2. **Database Schema**: Run EF Core migrations on PostgreSQL before migrating data:
   ```bash
   cd RedMist.Database
   dotnet ef database update --context TsContextPostgreSQL --connection ""
   ```
3. **Create OrganizationExtView** (Optional - if not already created): 
   The view provides a fallback to default organization logos. You can create it manually:
   ```bash
   # Using psql
   psql -h your-server -U postgres -d redmist-timing-dev -f RedMist.Database/SQL/PostgreSQL/CreateOrganizationExtView.sql
   
   # Or copy/paste the SQL from RedMist.Database/SQL/PostgreSQL/CreateOrganizationExtView.sql
   ```
   
   **Note**: If the view doesn't exist, the migration will still work. The view is only needed for displaying organization logos with default fallback.

## Configuration

### Option 1: User Secrets (Recommended for Development)

```bash
cd RedMist.PostgresMigration
dotnet user-secrets set "ConnectionStrings:SqlServer" "Server=localhost;Database=redmist-timing-dev;User Id=sa;Password=;TrustServerCertificate=True"
dotnet user-secrets set "ConnectionStrings:PostgreSQL" "Host=localhost;Database=redmist-timing-dev;Username=postgres;Password="
```

### Option 2: appsettings.json

Edit `appsettings.json`:

```json
{
  "ConnectionStrings": {
 "SqlServer": "Server=localhost;Database=redmist-timing-dev;User Id=sa;Password=;TrustServerCertificate=True",
    "PostgreSQL": "Host=localhost;Database=redmist-timing-dev;Username=postgres;Password="
  },
  "Migration": {
    "BatchSize": 1000,
    "EnableDetailedLogging": false
  }
}
```

## Running the Migration

```bash
cd RedMist.PostgresMigration
dotnet run
```

The tool will:
1. Verify connections to both databases
2. Ask for confirmation before proceeding
3. Migrate all tables in dependency order
4. Show progress for each table
5. Use batching for large tables (EventStatusLogs, CarLapLogs, X2Passings)

## Migration Order

The tool migrates tables in this order to respect foreign key dependencies:

1. Organizations
2. DefaultOrgImages
3. GoogleSheetsConfigs
4. UserOrganizationMappings
5. RelayLogs
6. UIVersions
7. Events
8. Sessions
9. CompetitorMetadata
10. EventStatusLogs (batched)
11. CarLapLogs (batched)
12. CarLastLaps
13. FlagLogs
14. SessionResults
15. X2Loops
16. X2Passings (batched)

## Features

- **Batched Processing**: Large tables are processed in configurable batches (default: 1000 records)
- **Progress Tracking**: Real-time progress updates for each table
- **Error Handling**: Detailed error messages and stack traces
- **Cancellation Support**: Press Ctrl+C to cancel migration
- **Connection Verification**: Validates both database connections before starting
- **JSON Column Support**: Properly handles JSON/JSONB columns
- **DateTime Compatibility**: Uses Npgsql legacy timestamp behavior (enabled at application startup) to handle SQL Server DateTime values. The database uses `timestamp without time zone` columns to match SQL Server's datetime behavior.

## Important Notes

### DateTime Handling

The migration tool uses Npgsql's legacy timestamp behavior to handle SQL Server datetime values:

1. **AppContext Switch**: Set at the very start of `Program.Main()` before any database operations
2. **Column Type**: PostgreSQL migrations create `timestamp without time zone` columns (not `timestamp with time zone`)
3. **No Conversion Needed**: DateTime values are migrated as-is from SQL Server without timezone conversion

If you see "Cannot write DateTime with Kind=Unspecified" errors:
- Ensure you're running the latest version of the migration tool
- Verify that the PostgreSQL schema was created with `TsContextPostgreSQL`
- Check that the `UpdatePostgreSQLContext` migration was applied (converts columns to `timestamp without time zone`)

## Post-Migration Steps

After successful migration:

1. **Verify Data Integrity**:
   ```sql
   -- Compare record counts
   SELECT COUNT(*) FROM "Organizations";
   SELECT COUNT(*) FROM "Events";
   SELECT COUNT(*) FROM "Sessions";
   SELECT COUNT(*) FROM "EventStatusLogs";
   -- etc.
   ```

2. **Verify OrganizationExtView**:
   ```sql
   -- Test the view
   SELECT * FROM public."OrganizationExtView" LIMIT 5;
 ```

3. **Update Application Configuration**:
   - Set `DatabaseProvider` to `PostgreSQL` in appsettings
   - Update connection strings to point to PostgreSQL
   - Ensure applications use `TsContextPostgreSQL` for PostgreSQL operations

4. **Test Application**:
   - Run integration tests
   - Verify all API endpoints
   - Check SignalR hubs
   - Test timing data processing
   - Verify the OrganizationExtView is properly accessed via the extension method

5. **Update Production**:
   - Update Kubernetes ConfigMaps/Secrets
   - Update Helm chart values
   - Deploy with PostgreSQL configuration

## Troubleshooting

### Connection Errors

- Verify PostgreSQL is running: `pg_isready`
- Check firewall settings
- Validate connection strings
- Ensure PostgreSQL user has necessary permissions

### Migration Failures

- Check logs for detailed error messages
- Verify PostgreSQL schema is up to date
- Ensure sufficient disk space
- Check PostgreSQL max_connections setting
- **DateTime errors**: The tool automatically converts SQL Server DateTime values to UTC for PostgreSQL. If you see "Cannot write DateTime with Kind=Unspecified" errors, ensure you're using the latest version of the migration tool.

### Performance

- Adjust `BatchSize` in configuration for optimal performance
- Consider increasing PostgreSQL `shared_buffers` and `work_mem`
- Disable PostgreSQL indexes temporarily for large migrations (re-enable after)

## Rollback

This tool does NOT delete or modify source SQL Server data. To rollback:

1. Truncate PostgreSQL tables
2. Re-run the migration tool
3. Or restore from SQL Server backup

## Advanced Usage

### Custom Batch Size

```bash
dotnet run -- --Migration:BatchSize=5000
```

### Enable Detailed Logging

```bash
dotnet run -- --Migration:EnableDetailedLogging=true
```

### Migration Specific Tables Only

Edit `DataMigrationService.cs` and comment out unwanted migrations in `MigrateAllDataAsync()`.

## Support

For issues or questions:
- Check application logs
- Review PostgreSQL logs
- Consult EF Core documentation: https://learn.microsoft.com/en-us/ef/core/
- Npgsql documentation: https://www.npgsql.org/
