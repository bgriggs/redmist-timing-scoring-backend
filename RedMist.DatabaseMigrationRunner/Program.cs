using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using RedMist.Database;

namespace RedMist.DatabaseMigrationRunner;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
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

            builder.Services.AddDbContext<TsContext>(options =>
                options.UseSqlServer(sqlConn)
                       .LogTo(Console.WriteLine, LogLevel.Debug));

            var host = builder.Build();

            var logger = host.Services.GetRequiredService<ILogger<Program>>();

            // Log startup information
            LogAssemblyInfo(logger);
            logger.LogInformation("Database Migration Runner starting...");
            logger.LogInformation("Connection string configured: {HasConnection}", !string.IsNullOrEmpty(sqlConn));
            logger.LogInformation("Starting database migrations...");

            using var scope = host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TsContext>();

            // Test database connectivity
            logger.LogInformation("Testing database connectivity...");
            await context.Database.CanConnectAsync();
            logger.LogInformation("Database connection successful");

            // Get pending migrations
            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
            var pendingCount = pendingMigrations.Count();

            if (pendingCount > 0)
            {
                logger.LogInformation("Found {Count} pending migrations: {Migrations}",
                    pendingCount, string.Join(", ", pendingMigrations));

                // Apply migrations
                await context.Database.MigrateAsync();
                logger.LogInformation("Successfully applied {Count} migrations", pendingCount);
            }
            else
            {
                logger.LogInformation("No pending migrations found. Database is up to date.");
            }

            logger.LogInformation("Migration process completed successfully.");

            // Give Kubernetes time to capture logs before container exits
            await Task.Delay(6000);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while applying migrations: {ex}");

            // Give Kubernetes time to capture error logs
            await Task.Delay(30000);
            return 1;
        }
    }

    private static void LogAssemblyInfo(ILogger logger)
    {
        var assembly = typeof(Program).Assembly;
        var name = assembly.GetName().Name ?? "unknown";
        var version = assembly.GetName().Version?.ToString() ?? "unknown";

        logger.LogInformation("Service starting...");
        logger.LogInformation("Assembly: {AssemblyName}, Version: {Version}", name, version);
    }    
}
