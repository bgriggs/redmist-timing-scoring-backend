using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared;
using RedMist.Database;

namespace RedMist.DatabaseMigrationRunner;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        
        // Clear default providers and add NLog
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        // Configure the database connection
        string sqlConn = builder.Configuration.GetConnectionString("Default")
            ?? throw new ArgumentNullException("ConnectionStrings:Default is required");

        builder.Services.AddDbContext<TsContext>(options => 
            options.UseSqlServer(sqlConn)
                   .LogTo(Console.WriteLine, LogLevel.Information));

        var host = builder.Build();
        
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        
        // Log startup information
        host.LogAssemblyInfo<Program>();
        logger.LogInformation("Database Migration Runner starting...");
        logger.LogInformation("Connection string configured: {HasConnection}", !string.IsNullOrEmpty(sqlConn));

        try
        {
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
            await Task.Delay(5000);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while applying migrations: {Message}", ex.Message);
            
            // Log additional details for troubleshooting
            logger.LogError("Connection string (masked): {ConnectionStringMasked}", 
                MaskConnectionString(sqlConn));
            
            // Give Kubernetes time to capture error logs
            await Task.Delay(5000);
            return 1;
        }
    }
    
    private static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return "null or empty";
            
        // Simple masking - replace password value
        return System.Text.RegularExpressions.Regex.Replace(
            connectionString, 
            @"(Password|Pwd)=([^;]+)", 
            "$1=***", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
