using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.Database.PostgreSQL;
using RedMist.TimingCommon;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Reflection;

namespace RedMist.PostgresMigration.Services;

/// <summary>
/// Service responsible for migrating data from SQL Server to PostgreSQL
/// </summary>
public class DataMigrationService
{
    private readonly IDbContextFactory<TsContext> _sqlServerContextFactory;
    private readonly IDbContextFactory<TsContextPostgreSQL> _postgresContextFactory;
    private readonly ILogger<DataMigrationService> _logger;
    private readonly int _batchSize;

    public DataMigrationService(
        IDbContextFactory<TsContext> sqlServerContextFactory,
        IDbContextFactory<TsContextPostgreSQL> postgresContextFactory,
        ILogger<DataMigrationService> logger,
        int batchSize = 1000)
    {
        _sqlServerContextFactory = sqlServerContextFactory;
        _postgresContextFactory = postgresContextFactory;
        _logger = logger;
        _batchSize = batchSize;
    }

    ///// <summary>
    ///// Converts all DateTime properties in an entity to UTC.
    ///// PostgreSQL requires DateTimes to have Kind=Utc when using timestamp with time zone.
    ///// </summary>
    //private void ConvertDateTimesToUtc<T>(T entity) where T : class
    //{
    //    if (entity == null) return;

    //    var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
    //        .Where(p => p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTime?));

    //    foreach (var prop in properties)
    //    {
    //        var value = prop.GetValue(entity);

    //        // Handle non-nullable DateTime
    //        if (value is DateTime dt)
    //        {
    //            if (dt.Kind == DateTimeKind.Unspecified)
    //            {
    //                prop.SetValue(entity, dt);
    //            }
    //            else if (dt.Kind == DateTimeKind.Local)
    //            {
    //                // Convert local to UTC
    //                prop.SetValue(entity, dt.ToUniversalTime());
    //            }
    //        }
    //    }
    //}

    ///// <summary>
    ///// Converts DateTime values in a collection of entities to UTC.
    ///// </summary>
    //private void ConvertDateTimesToUtc<T>(IEnumerable<T> entities) where T : class
    //{
    //    foreach (var entity in entities)
    //    {
    //        ConvertDateTimesToUtc(entity);
    //    }
    //}

    public async Task MigrateAllDataAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting complete data migration from SQL Server to PostgreSQL");

        try
        {
            // Verify connections
            await VerifyConnectionsAsync(cancellationToken);

            // Migrate in dependency order
            await MigrateOrganizationsAsync(cancellationToken);
            await MigrateDefaultOrgImagesAsync(cancellationToken);
            await MigrateGoogleSheetsConfigsAsync(cancellationToken);
            await MigrateUserOrganizationMappingsAsync(cancellationToken);
            //await MigrateRelayLogsAsync(cancellationToken);
            //await MigrateUIVersionsAsync(cancellationToken);
            await MigrateEventsAsync(cancellationToken);
            await MigrateSessionsAsync(cancellationToken);
            await MigrateCompetitorMetadataAsync(cancellationToken);
            //await MigrateEventStatusLogsAsync(cancellationToken);
            await MigrateCarLapLogsAsync(cancellationToken);
            await MigrateCarLastLapsAsync(cancellationToken);
            await MigrateFlagLogsAsync(cancellationToken);
            await MigrateSessionResultsAsync(cancellationToken);
            //await MigrateX2LoopsAsync(cancellationToken);
            //await MigrateX2PassingsAsync(cancellationToken);

            _logger.LogInformation("Data migration completed successfully!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data migration failed");
            throw;
        }
    }

    private async Task VerifyConnectionsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Verifying database connections...");

        await using var sqlContext = await _sqlServerContextFactory.CreateDbContextAsync(cancellationToken);
        await using var pgContext = await _postgresContextFactory.CreateDbContextAsync(cancellationToken);

        var sqlCanConnect = await sqlContext.Database.CanConnectAsync(cancellationToken);
        var pgCanConnect = await pgContext.Database.CanConnectAsync(cancellationToken);

        if (!sqlCanConnect)
            throw new InvalidOperationException("Cannot connect to SQL Server database");
        if (!pgCanConnect)
            throw new InvalidOperationException("Cannot connect to PostgreSQL database");

        _logger.LogInformation("Successfully verified both database connections");

        // Log PostgreSQL column types for DateTime columns to verify migration
        try
        {
            var timestampType = await pgContext.Database.ExecuteSqlRawAsync(
          @"SELECT data_type FROM information_schema.columns 
           WHERE table_name = 'RelayLogs' AND column_name = 'Timestamp'",
       cancellationToken);
            _logger.LogInformation("PostgreSQL timestamp column configuration verified");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify PostgreSQL column types");
        }
    }

    private async Task MigrateOrganizationsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<Organization>("Organizations",
            async (sqlContext, pgContext) =>
            {
                var orgs = await sqlContext.Organizations.AsNoTracking().ToListAsync(cancellationToken);
                if (orgs.Any())
                {
                    // Temporarily allow identity insert for PostgreSQL
                    await pgContext.Database.ExecuteSqlRawAsync(
                        "ALTER TABLE \"Organizations\" ALTER COLUMN \"Id\" DROP IDENTITY IF EXISTS;",
                        cancellationToken);
                
                    await pgContext.Organizations.AddRangeAsync(orgs, cancellationToken);
                    await pgContext.SaveChangesAsync(cancellationToken);
                
                    // Get the max ID to reset the sequence
                    var maxId = orgs.Max(o => o.Id);
                
                    // Re-add identity and set the sequence to continue from max ID
                    await pgContext.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE \"Organizations\" ALTER COLUMN \"Id\" ADD GENERATED ALWAYS AS IDENTITY;",
                        cancellationToken);
                
                    await pgContext.Database.ExecuteSqlRawAsync(
                        $"SELECT setval(pg_get_serial_sequence('\"Organizations\"', 'Id'), {maxId}, true);",
                        cancellationToken);
                }
                return orgs.Count;
            }, cancellationToken);
    }

    private async Task MigrateDefaultOrgImagesAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<DefaultOrgImage>("DefaultOrgImages",
           async (sqlContext, pgContext) =>
           {
               var images = await sqlContext.DefaultOrgImages.AsNoTracking().ToListAsync(cancellationToken);
               if (images.Any())
               {
                   await pgContext.DefaultOrgImages.AddRangeAsync(images, cancellationToken);
                   await pgContext.SaveChangesAsync(cancellationToken);
               }
               return images.Count;
           }, cancellationToken);
    }

    private async Task MigrateGoogleSheetsConfigsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<GoogleSheetsConfig>("GoogleSheetsConfigs",
            async (sqlContext, pgContext) =>
            {
                var configs = await sqlContext.GoogleSheetsConfigs.AsNoTracking().ToListAsync(cancellationToken);
                if (configs.Any())
                {
                    await pgContext.GoogleSheetsConfigs.AddRangeAsync(configs, cancellationToken);
                    await pgContext.SaveChangesAsync(cancellationToken);
                }
                return configs.Count;
            }, cancellationToken);
    }

    private async Task MigrateUserOrganizationMappingsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<UserOrganizationMapping>("UserOrganizationMappings",
           async (sqlContext, pgContext) =>
           {
               var mappings = await sqlContext.UserOrganizationMappings.AsNoTracking().ToListAsync(cancellationToken);
               if (mappings.Any())
               {
                   await pgContext.UserOrganizationMappings.AddRangeAsync(mappings, cancellationToken);
                   await pgContext.SaveChangesAsync(cancellationToken);
               }
               return mappings.Count;
           }, cancellationToken);
    }

    //   private async Task MigrateRelayLogsAsync(CancellationToken cancellationToken)
    //   {
    //       await MigrateLargeEntityAsync<RelayLog>("RelayLogs",
    //          async (sqlContext, skip, take, ct) =>
    // {
    //     var logs = await sqlContext.RelayLogs
    //          .AsNoTracking()
    //      .OrderBy(r => r.Id)
    //         .Skip(skip)
    //           .Take(take)
    //            .ToListAsync(ct);
    //     ConvertDateTimesToUtc(logs);
    //     return logs;
    // },
    //           async (sqlContext, ct) => await sqlContext.RelayLogs.CountAsync(ct),
    //cancellationToken);
    //   }

    private async Task MigrateUIVersionsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<UIVersionInfo>("UIVersions",
        async (sqlContext, pgContext) =>
       {
           var versions = await sqlContext.UIVersions.AsNoTracking().ToListAsync(cancellationToken);
           if (versions.Any())
           {
               // UIVersionInfo has no key, use ExecuteSql to insert
               foreach (var version in versions)
               {
                   // ExecuteSqlRawAsync with parameters - cancellationToken is a separate argument
                   await pgContext.Database.ExecuteSqlRawAsync(
                       @"INSERT INTO ""UIVersions"" 
(""LatestAndroidVersion"", ""LatestIOSVersion"", ""LatestWebVersion"",
       ""MinimumAndroidVersion"", ""MinimumIOSVersion"", ""MinimumWebVersion"",
 ""IsAndroidMinimumMandatory"", ""IsIOSMinimumMandatory"", ""IsWebMinimumMandatory"",
    ""RecommendAndroidUpdate"", ""RecommendIOSUpdate"", ""RecommendWebUpdate"")
       VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11})",
                           new object[]
                     {
   version.LatestAndroidVersion, version.LatestIOSVersion, version.LatestWebVersion,
         version.MinimumAndroidVersion, version.MinimumIOSVersion, version.MinimumWebVersion,
      version.IsAndroidMinimumMandatory, version.IsIOSMinimumMandatory, version.IsWebMinimumMandatory,
           version.RecommendAndroidUpdate, version.RecommendIOSUpdate, version.RecommendWebUpdate
                },
                       cancellationToken);
               }
           }
           return versions.Count;
       },
          cancellationToken);
    }

    private async Task MigrateEventsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<TimingCommon.Models.Configuration.Event>("Events",
           async (sqlContext, pgContext) =>
          {
              var events = await sqlContext.Events.AsNoTracking().ToListAsync(cancellationToken);
              if (events.Any())
              {
                  // Temporarily allow identity insert for PostgreSQL
                  await pgContext.Database.ExecuteSqlRawAsync(
                      "ALTER TABLE \"Events\" ALTER COLUMN \"Id\" DROP IDENTITY IF EXISTS;",
                      cancellationToken);
              
                  await pgContext.Events.AddRangeAsync(events, cancellationToken);
                  await pgContext.SaveChangesAsync(cancellationToken);
              
                  // Get the max ID to reset the sequence
                  var maxId = events.Max(e => e.Id);
              
                  // Re-add identity and set the sequence to continue from max ID
                  await pgContext.Database.ExecuteSqlRawAsync(
                      $"ALTER TABLE \"Events\" ALTER COLUMN \"Id\" ADD GENERATED ALWAYS AS IDENTITY;",
                      cancellationToken);
              
                  await pgContext.Database.ExecuteSqlRawAsync(
                      $"SELECT setval(pg_get_serial_sequence('\"Events\"', 'Id'), {maxId}, true);",
                      cancellationToken);
              }
              return events.Count;
          }, cancellationToken);
    }

    private async Task MigrateSessionsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<Session>("Sessions",
       async (sqlContext, pgContext) =>
             {
                 var sessions = await sqlContext.Sessions.AsNoTracking().ToListAsync(cancellationToken);
                 //ConvertDateTimesToUtc(sessions);
                 if (sessions.Any())
                 {
                     await pgContext.Sessions.AddRangeAsync(sessions, cancellationToken);
                     await pgContext.SaveChangesAsync(cancellationToken);
                 }
                 return sessions.Count;
             }, cancellationToken);
    }

    private async Task MigrateCompetitorMetadataAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<CompetitorMetadata>("CompetitorMetadata",
     async (sqlContext, pgContext) =>
 {
     var metadata = await sqlContext.CompetitorMetadata.AsNoTracking().ToListAsync(cancellationToken);
     //ConvertDateTimesToUtc(metadata);
     if (metadata.Any())
     {
         await pgContext.CompetitorMetadata.AddRangeAsync(metadata, cancellationToken);
         await pgContext.SaveChangesAsync(cancellationToken);
     }
     return metadata.Count;
 }, cancellationToken);
    }

    private async Task MigrateEventStatusLogsAsync(CancellationToken cancellationToken)
    {
        await MigrateLargeEntityAsync<EventStatusLog>("EventStatusLogs",
      async (sqlContext, skip, take, ct) =>
         {
             var logs = await sqlContext.EventStatusLogs
       .AsNoTracking()
    .OrderBy(e => e.Id)
   .Skip(skip)
      .Take(take)
     .ToListAsync(ct);
             //ConvertDateTimesToUtc(logs);
             return logs;
         },
  async (sqlContext, ct) => await sqlContext.EventStatusLogs.CountAsync(ct),
 cancellationToken);
    }

    private async Task MigrateCarLapLogsAsync(CancellationToken cancellationToken)
    {
        await MigrateLargeEntityAsync<CarLapLog>("CarLapLogs",
         async (sqlContext, skip, take, ct) =>
          {
              var logs = await sqlContext.CarLapLogs
         .AsNoTracking()
      .OrderBy(c => c.Id)
         .Skip(skip)
          .Take(take)
           .ToListAsync(ct);
              //ConvertDateTimesToUtc(logs);
              return logs;
          },
                async (sqlContext, ct) => await sqlContext.CarLapLogs.CountAsync(ct),
      cancellationToken);
    }

    private async Task MigrateCarLastLapsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<CarLastLap>(
            "CarLastLaps",
            async (sqlContext, pgContext) =>
          {
              var laps = await sqlContext.CarLastLaps.AsNoTracking().ToListAsync(cancellationToken);
              //ConvertDateTimesToUtc(laps);
              if (laps.Any())
              {
                  await pgContext.CarLastLaps.AddRangeAsync(laps, cancellationToken);
                  await pgContext.SaveChangesAsync(cancellationToken);
              }
              return laps.Count;
          },
           cancellationToken);
    }

    private async Task MigrateFlagLogsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<FlagLog>(
 "FlagLogs",
       async (sqlContext, pgContext) =>
            {
                var flags = await sqlContext.FlagLog.AsNoTracking().ToListAsync(cancellationToken);
                //ConvertDateTimesToUtc(flags);
                if (flags.Any())
                {
                    await pgContext.FlagLog.AddRangeAsync(flags, cancellationToken);
                    await pgContext.SaveChangesAsync(cancellationToken);
                }
                return flags.Count;
            },
     cancellationToken);
    }

    private async Task MigrateSessionResultsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<SessionResult>(
       "SessionResults",
       async (sqlContext, pgContext) =>
      {
          // Use raw SQL to read JSON columns as strings
          var rawResults = await sqlContext.Database
                  .SqlQueryRaw<SessionResultRaw>(@"
          SELECT 
     EventId, 
            SessionId, 
            Start,
         CAST(Payload AS NVARCHAR(MAX)) AS PayloadJson,
  CAST(SessionState AS NVARCHAR(MAX)) AS SessionStateJson
           FROM SessionResults")
              .AsNoTracking()
          .ToListAsync(cancellationToken);

          var validResults = new List<SessionResult>();
          var skippedCount = 0;

          foreach (var raw in rawResults)
          {
              try
              {
                  var result = new SessionResult
                  {
                      EventId = raw.EventId,
                      SessionId = raw.SessionId,
                      Start = raw.Start
                  };

                  // Try to deserialize Payload if not null
                  if (!string.IsNullOrEmpty(raw.PayloadJson))
                  {
                      try
                      {
                          result.Payload = System.Text.Json.JsonSerializer.Deserialize<Payload>(raw.PayloadJson);
                      }
                      catch (System.Text.Json.JsonException ex)
                      {
                          _logger.LogWarning("Skipping Payload for EventId={EventId}, SessionId={SessionId}: {Message}",
                         raw.EventId, raw.SessionId, ex.Message);
                          // Set to null if deserialization fails
                          result.Payload = null;
                      }
                  }

                  // Try to deserialize SessionState if not null
                  if (!string.IsNullOrEmpty(raw.SessionStateJson))
                  {
                      try
                      {
                          result.SessionState = System.Text.Json.JsonSerializer.Deserialize<SessionState>(raw.SessionStateJson);
                      }
                      catch (System.Text.Json.JsonException ex)
                      {
                          _logger.LogWarning("Skipping SessionState for EventId={EventId}, SessionId={SessionId}: {Message}",
                             raw.EventId, raw.SessionId, ex.Message);
                          // Set to null if deserialization fails
                          result.SessionState = null;
                      }
                  }

                  //ConvertDateTimesToUtc(result);
                  validResults.Add(result);
              }
              catch (Exception ex)
              {
                  _logger.LogWarning(ex, "Skipping SessionResult for EventId={EventId}, SessionId={SessionId}: {Message}",
                      raw.EventId, raw.SessionId, ex.Message);
                  skippedCount++;
              }
          }

          if (validResults.Any())
          {
              await pgContext.SessionResults.AddRangeAsync(validResults, cancellationToken);
              await pgContext.SaveChangesAsync(cancellationToken);
          }

          if (skippedCount > 0)
          {
              _logger.LogWarning("Migrated {ValidCount} of {TotalCount} SessionResults (skipped {SkippedCount} records with invalid JSON)",
             validResults.Count, rawResults.Count, skippedCount);
          }

          return rawResults.Count;
      },
  cancellationToken);
    }

    private async Task MigrateX2LoopsAsync(CancellationToken cancellationToken)
    {
        await MigrateEntityAsync<Loop>("X2Loops",
        async (sqlContext, pgContext) =>
          {
              var loops = await sqlContext.X2Loops.AsNoTracking().ToListAsync(cancellationToken);
              if (loops.Any())
              {
                  await pgContext.X2Loops.AddRangeAsync(loops, cancellationToken);
                  await pgContext.SaveChangesAsync(cancellationToken);
              }
              return loops.Count;
          },
           cancellationToken);
    }

    private async Task MigrateX2PassingsAsync(CancellationToken cancellationToken)
    {
        await MigrateLargeEntityAsync<Passing>(
       "X2Passings",
     async (sqlContext, skip, take, ct) =>
         {
             var passings = await sqlContext.X2Passings
        .AsNoTracking()
           .OrderBy(p => p.OrganizationId)
    .ThenBy(p => p.EventId)
          .ThenBy(p => p.Id)
      .Skip(skip)
  .Take(take)
      .ToListAsync(ct);
             //ConvertDateTimesToUtc(passings);
             return passings;
         },
            async (sqlContext, ct) => await sqlContext.X2Passings.CountAsync(ct),
        cancellationToken);
    }

    private async Task MigrateEntityAsync<T>(
        string entityName,
     Func<TsContext, TsContextPostgreSQL, Task<int>> migrateFunc,
CancellationToken cancellationToken) where T : class
    {
        _logger.LogInformation("Migrating {EntityName}...", entityName);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await using var sqlContext = await _sqlServerContextFactory.CreateDbContextAsync(cancellationToken);
        await using var pgContext = await _postgresContextFactory.CreateDbContextAsync(cancellationToken);

        var count = await migrateFunc(sqlContext, pgContext);

        stopwatch.Stop();
        _logger.LogInformation("Migrated {Count} {EntityName} records in {Elapsed}ms",
             count, entityName, stopwatch.ElapsedMilliseconds);
    }

    private async Task MigrateLargeEntityAsync<T>(
     string entityName,
   Func<TsContext, int, int, CancellationToken, Task<List<T>>> fetchBatchFunc,
  Func<TsContext, CancellationToken, Task<int>> countFunc,
  CancellationToken cancellationToken) where T : class
    {
        _logger.LogInformation("Migrating {EntityName} (large table, using batches)...", entityName);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await using var sqlContext = await _sqlServerContextFactory.CreateDbContextAsync(cancellationToken);
        var totalCount = await countFunc(sqlContext, cancellationToken);

        if (totalCount == 0)
        {
            _logger.LogInformation("No {EntityName} records to migrate", entityName);
            return;
        }

        _logger.LogInformation("Total {EntityName} records to migrate: {Count}", entityName, totalCount);

        var batches = (int)Math.Ceiling(totalCount / (double)_batchSize);
        var migratedCount = 0;

        for (int i = 0; i < batches; i++)
        {
            var skip = i * _batchSize;
            _logger.LogInformation("Migrating {EntityName} batch {Current}/{Total} (records {Skip}-{End})",
           entityName, i + 1, batches, skip, Math.Min(skip + _batchSize, totalCount));

            await using var batchSqlContext = await _sqlServerContextFactory.CreateDbContextAsync(cancellationToken);
            await using var batchPgContext = await _postgresContextFactory.CreateDbContextAsync(cancellationToken);

            var batch = await fetchBatchFunc(batchSqlContext, skip, _batchSize, cancellationToken);
            if (batch.Any())
            {
                await batchPgContext.Set<T>().AddRangeAsync(batch, cancellationToken);
                await batchPgContext.SaveChangesAsync(cancellationToken);
                migratedCount += batch.Count;
            }
        }

        stopwatch.Stop();
        _logger.LogInformation("Migrated {Count} {EntityName} records in {Elapsed}ms",
              migratedCount, entityName, stopwatch.ElapsedMilliseconds);
    }

    // Helper class for raw SessionResult data
    private class SessionResultRaw
    {
        public int EventId { get; set; }
        public int SessionId { get; set; }
        public DateTime Start { get; set; }
        public string? PayloadJson { get; set; }
        public string? SessionStateJson { get; set; }
    }
}
