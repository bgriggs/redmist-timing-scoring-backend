using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using RedMist.Database;
using RedMist.Database.Models;

namespace RedMist.UpdateFlagtronicsDriverNames;

internal class Program
{
    const string NameCsvPath = @"C:\Users\brian\OneDrive\Documents\WRL-ECR-Drivers-2026.csv";
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Ensure user secrets are loaded in development
        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddUserSecrets<Program>();
        }

        // Clear default providers and add NLog
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        // Configure the database connection
        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");

        // Enable legacy timestamp behavior for PostgreSQL
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        builder.Services.AddDbContext<TsContext>(options =>
            options.UseNpgsql(sqlConn)
                   .LogTo(Console.WriteLine, LogLevel.Debug));

        var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();

        // Log startup information
        logger.LogInformation("Driver name update starting...");
        logger.LogInformation("Connection string configured: {HasConnection}", !string.IsNullOrEmpty(sqlConn));
        logger.LogInformation("Loading CSV driver name from {CsvPath}", NameCsvPath);
        logger.LogInformation("Updating names in database...");

        // Read CSV file and process driver names
        var driverNames = new List<DriverInfo>();
        const int maxNameLength = 50;

        using (var reader = new StreamReader(NameCsvPath))
        {
            // Skip header row
            await reader.ReadLineAsync();

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var columns = line.Split(',');
                if (columns.Length < 2)
                {
                    logger.LogWarning("Skipping invalid line: {Line}", line);
                    continue;
                }

                // Parse FlagtronicsId from column 0
                if (!long.TryParse(columns[0].Trim(), out long flagtronicsId))
                {
                    logger.LogWarning("Invalid FlagtronicsId in line: {Line}", line);
                    continue;
                }

                // Get name from column 1
                var name = columns[1].Trim();

                // Validate name
                if (string.IsNullOrEmpty(name))
                {
                    logger.LogWarning("Empty name for FlagtronicsId {FlagtronicsId}, skipping", flagtronicsId);
                    continue;
                }

                // Clip name if it exceeds max length
                if (name.Length > maxNameLength)
                {
                    logger.LogWarning("Name '{Name}' exceeds max length of {MaxLength}, clipping to fit", name, maxNameLength);
                    name = name[..maxNameLength];
                }

                // Create DriverInfo object
                var driverInfo = new DriverInfo
                {
                    FlagtronicsId = flagtronicsId,
                    Name = name
                };

                driverNames.Add(driverInfo);
            }
        }

        logger.LogInformation("Loaded {Count} driver names from CSV", driverNames.Count);

        // Consolidate duplicate FlagtronicsId and Name pairs
        var originalCount = driverNames.Count;
        driverNames = driverNames
            .GroupBy(d => new { d.FlagtronicsId, d.Name })
            .Select(g => g.First())
            .ToList();

        var consolidatedCount = originalCount - driverNames.Count;
        if (consolidatedCount > 0)
        {
            logger.LogInformation("Consolidated {Count} duplicate FlagtronicsId/Name pairs", consolidatedCount);
        }

        // Check for duplicate FlagtronicsIds
        var duplicateFlagtronicsIds = driverNames
            .GroupBy(d => d.FlagtronicsId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateFlagtronicsIds.Count > 0)
        {
            logger.LogError("Duplicate FlagtronicsIds found in CSV. Cannot proceed with update.");
            foreach (var duplicateId in duplicateFlagtronicsIds)
            {
                var duplicateDrivers = driverNames.Where(d => d.FlagtronicsId == duplicateId).ToList();
                logger.LogError("FlagtronicsId {FlagtronicsId} appears {Count} times with names: {Names}",
                    duplicateId, duplicateDrivers.Count, string.Join(", ", duplicateDrivers.Select(d => $"'{d.Name}'")));
            }
            return;
        }

        logger.LogInformation("All FlagtronicsIds are unique. Proceeding with database update.");

        // Update database
        using (var scope = host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TsContext>();

            // Get all existing driver records with FlagtronicsId
            var existingDrivers = await dbContext.DriverInfo
                .Where(d => d.FlagtronicsId != null)
                .ToListAsync();

            int updatedCount = 0;
            int insertedCount = 0;
            int unchangedCount = 0;

            foreach (var csvDriver in driverNames)
            {
                // Find existing driver by FlagtronicsId
                var existingDriver = existingDrivers
                    .FirstOrDefault(d => d.FlagtronicsId == csvDriver.FlagtronicsId);

                if (existingDriver != null)
                {
                    // Record exists - check if name needs updating
                    if (existingDriver.Name != csvDriver.Name)
                    {
                        logger.LogInformation("Updating driver {FlagtronicsId}: '{OldName}' -> '{NewName}'",
                            csvDriver.FlagtronicsId, existingDriver.Name, csvDriver.Name);
                        existingDriver.Name = csvDriver.Name;
                        updatedCount++;
                    }
                    else
                    {
                        logger.LogDebug("Driver {FlagtronicsId} name unchanged: '{Name}'",
                            csvDriver.FlagtronicsId, csvDriver.Name);
                        unchangedCount++;
                    }
                }
                else
                {
                    // Record doesn't exist - insert new
                    logger.LogInformation("Inserting new driver {FlagtronicsId}: '{Name}'",
                        csvDriver.FlagtronicsId, csvDriver.Name);
                    dbContext.DriverInfo.Add(csvDriver);
                    insertedCount++;
                }
            }

            // Save all changes to database
            await dbContext.SaveChangesAsync();

            logger.LogInformation("Database update complete. Inserted: {Inserted}, Updated: {Updated}, Unchanged: {Unchanged}",
                insertedCount, updatedCount, unchangedCount);
        }

        logger.LogInformation("Driver name update completed successfully");
    }
}
