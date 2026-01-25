using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using RedMist.Database;
using RedMist.TimingCommon.Extensions;

namespace RedMist.ConvertPayloadToSessionResults;

internal class Program
{
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
                   .LogTo(Console.WriteLine, LogLevel.Warning));

        var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();

        // Log startup information
        LogAssemblyInfo(logger);
        logger.LogInformation("Database Payload converter starting...");
        logger.LogInformation("Connection string configured: {HasConnection}", !string.IsNullOrEmpty(sqlConn));
        logger.LogInformation("Starting Payload converter...");

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TsContext>();

        // Test database connectivity
        logger.LogInformation("Testing database connectivity...");
        await context.Database.CanConnectAsync();
        logger.LogInformation("Database connection successful");

        var payloads = await context.SessionResults.Where(s => s.Payload != null).ToListAsync();
        logger.LogInformation("Found {Count} session results with payloads to convert", payloads.Count);
        foreach (var result in payloads)
        {
            try
            {
                var ss = result.Payload!.ToSessionState();
                result.SessionState = ss;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to convert payload for SessionResult ID {SessionResultId}", result.EventId);
            }
        }
        await context.SaveChangesAsync();
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
