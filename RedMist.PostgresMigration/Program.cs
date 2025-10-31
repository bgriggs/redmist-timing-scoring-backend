using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using RedMist.Database;
using RedMist.Database.PostgreSQL;
using RedMist.PostgresMigration.Services;

namespace RedMist.PostgresMigration;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        // CRITICAL: Set this BEFORE any Npgsql operations
        // This must be the very first thing in Main()
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        try
        {
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine("  RedMist SQL Server to PostgreSQL Migration Tool");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine();

            var builder = Host.CreateApplicationBuilder(args);

            // Ensure user secrets are loaded in development
            if (builder.Environment.IsDevelopment())
            {
                builder.Configuration.AddUserSecrets<Program>();
            }

            // Clear default providers and add NLog
            builder.Logging.ClearProviders();
            builder.Logging.AddNLog();

            // Get configuration
            var batchSize = builder.Configuration.GetValue("Migration:BatchSize", 1000);
            var enableDetailedLogging = builder.Configuration.GetValue("Migration:EnableDetailedLogging", false);

            var sqlServerConn = builder.Configuration["ConnectionStrings:SqlServer"]
                ?? throw new InvalidOperationException("SQL Server connection string not configured");
            var postgresConn = builder.Configuration["ConnectionStrings:PostgreSQL"]
                ?? throw new InvalidOperationException("PostgreSQL connection string not configured");

            // Register SQL Server context factory (TsContext)
            builder.Services.AddDbContextFactory<TsContext>(
                options =>
                {
                    options.UseSqlServer(sqlServerConn);
                    if (enableDetailedLogging)
                    {
                        options.EnableDetailedErrors();
                        options.EnableSensitiveDataLogging();
                    }
                },
                ServiceLifetime.Scoped);

            // Register PostgreSQL context factory (TsContextPostgreSQL)
            builder.Services.AddDbContextFactory<TsContextPostgreSQL>(
                options =>
                {
                    options.UseNpgsql(postgresConn);
                    if (enableDetailedLogging)
                    {
                        options.EnableDetailedErrors();
                        options.EnableSensitiveDataLogging();
                    }
                },
                ServiceLifetime.Scoped);

            // Register migration service
            builder.Services.AddScoped<DataMigrationService>(sp =>
            {
                var sqlServerFactory = sp.GetRequiredService<IDbContextFactory<TsContext>>();
                var postgresFactory = sp.GetRequiredService<IDbContextFactory<TsContextPostgreSQL>>();
                var logger = sp.GetRequiredService<ILogger<DataMigrationService>>();

                return new DataMigrationService(sqlServerFactory, postgresFactory, logger, batchSize);
            });

            var host = builder.Build();

            var logger = host.Services.GetRequiredService<ILogger<Program>>();

            // Log startup information
            LogAssemblyInfo(logger);
            logger.LogInformation("SQL Server Connection: {Connection}", MaskConnectionString(sqlServerConn));
            logger.LogInformation("PostgreSQL Connection: {Connection}", MaskConnectionString(postgresConn));
            logger.LogInformation("Batch Size: {BatchSize}", batchSize);
            logger.LogInformation("Legacy Timestamp Behavior: Enabled");
            Console.WriteLine();

            // Confirm before proceeding
            Console.WriteLine("⚠️  WARNING: This will migrate ALL data from SQL Server to PostgreSQL.");
            Console.WriteLine("⚠️  Ensure the PostgreSQL database schema is already created and up to date.");
            Console.WriteLine("⚠️  Existing data in PostgreSQL tables will be preserved (no truncation).");
            Console.WriteLine();
            Console.Write("Do you want to proceed? (yes/no): ");
            var confirmation = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (confirmation != "yes")
            {
                Console.WriteLine();
                Console.WriteLine("Migration cancelled by user.");
                return 0;
            }

            Console.WriteLine();
            logger.LogInformation("Starting migration process...");

            using var scope = host.Services.CreateScope();
            var migrationService = scope.ServiceProvider.GetRequiredService<DataMigrationService>();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                logger.LogWarning("Migration cancelled by user (Ctrl+C)");
            };

            await migrationService.MigrateAllDataAsync(cts.Token);

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine("  ✅ Migration completed successfully!");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine("1. Verify data integrity in PostgreSQL");
            Console.WriteLine("2. Update application configuration to use PostgreSQL");
            Console.WriteLine("3. Test all application functionality");
            Console.WriteLine("4. Update connection strings in production");
            Console.WriteLine();

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Migration was cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine("  ❌ Migration failed!");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Full error details:");
            Console.WriteLine(ex.ToString());
            Console.WriteLine();

            return 1;
        }
    }

    private static void LogAssemblyInfo(ILogger logger)
    {
        var assembly = typeof(Program).Assembly;
        var name = assembly.GetName().Name ?? "unknown";
        var version = assembly.GetName().Version?.ToString() ?? "unknown";

        logger.LogInformation("PostgreSQL Migration Tool");
        logger.LogInformation("Assembly: {AssemblyName}, Version: {Version}", name, version);
    }

    private static string MaskConnectionString(string connectionString)
    {
        // Mask sensitive parts of connection string
        var parts = connectionString.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Contains("password", StringComparison.OrdinalIgnoreCase) ||
                parts[i].Contains("pwd", StringComparison.OrdinalIgnoreCase))
            {
                var keyValue = parts[i].Split('=');
                if (keyValue.Length == 2)
                {
                    parts[i] = $"{keyValue[0]}=****";
                }
            }
        }
        return string.Join(';', parts);
    }
}
