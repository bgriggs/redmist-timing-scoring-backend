using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventOrchestration.Services;
using StackExchange.Redis;

namespace RedMist.EventOrchestration;

public class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--run-archive"))
        {
            await RunArchiveOnceAsync(args);
            return;
        }

        if (args.Contains("--run-simulated-event-purge"))
        {
            await RunSimulatedEventPurgeOnceAsync(args);
            return;
        }

        var builder = WebApplication.CreateBuilder(args);
        ConfigureCommonServices(builder);

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";
        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn, c => { c.AbortOnConnectFail = false; c.ConnectRetry = 10; c.ConnectTimeout = 10; }));

        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
        builder.Services.AddHealthChecks()
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"]);

        builder.Services.AddSingleton<EventsChecker>();
        builder.Services.AddHostedService<EventArchiveService>();
        builder.Services.AddHostedService<OrchestrationService>();
        builder.Services.AddHostedService<RelayLogCleanupService>();

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Event Orchestration";
            app.MapOpenApi();
        }

        app.MapHealthChecks("/healthz/startup", new HealthCheckOptions
        {
            Predicate = _ => true, // Run all checks
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
        }).AllowAnonymous();
        app.MapHealthChecks("/healthz/live", new HealthCheckOptions
        {
            Predicate = _ => false, // Only check that service is not locked up
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
        }).AllowAnonymous();
        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            Predicate = _ => true, // Run all checks
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
        }).AllowAnonymous();

        app.UseAuthorization();
        app.MapControllers();

        await app.RunAsync();
    }

    private static void ConfigureCommonServices(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

        builder.Services.AddHttpClient();
        builder.Services.AddTransient<IArchiveStorage, BunnyArchiveStorage>();
        builder.Services.AddTransient<EmailHelper>();
    }

    private static async Task RunArchiveOnceAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ConfigureCommonServices(builder);

        var serviceProvider = builder.Services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation("Starting archive process with --run-archive flag...");

        try
        {
            var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<TsContext>>();
            var archiveStorage = serviceProvider.GetRequiredService<IArchiveStorage>();
            var emailHelper = serviceProvider.GetRequiredService<EmailHelper>();

            var archiveService = new EventArchiveService(loggerFactory, dbContextFactory, archiveStorage, emailHelper);

            const int maxRetriesPerDay = 3;
            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                logger.LogWarning("Cancellation requested. Stopping archive process...");
            };

            await archiveService.RunArchiveProcessWithRetriesAsync(maxRetriesPerDay, cts.Token);

            logger.LogInformation("Archive process completed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Archive process failed with exception.");
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunSimulatedEventPurgeOnceAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ConfigureCommonServices(builder);

        var serviceProvider = builder.Services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation("Starting simulated event purge with --run-simulated-event-purge flag...");

        try
        {
            var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<TsContext>>();
            var archiveStorage = serviceProvider.GetRequiredService<IArchiveStorage>();
            var emailHelper = serviceProvider.GetRequiredService<EmailHelper>();

            var archiveService = new EventArchiveService(loggerFactory, dbContextFactory, archiveStorage, emailHelper);

            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                logger.LogWarning("Cancellation requested. Stopping simulated event purge...");
            };

            await archiveService.RunSimulatedEventPurgeAsync(cts.Token);

            logger.LogInformation("Simulated event purge completed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Simulated event purge failed with exception.");
            Environment.ExitCode = 1;
        }
    }
}
