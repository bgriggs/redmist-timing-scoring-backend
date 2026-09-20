using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using NLog.Extensions.Logging;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.EventLogger.Services;
using StackExchange.Redis;

namespace RedMist.EventLogger;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";
        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn, c => { c.AbortOnConnectFail = false; c.ConnectRetry = 10; c.ConnectTimeout = 10; }));
        builder.Services.AddHybridCache(o => o.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromMinutes(15) });

        builder.Services.AddHealthChecks()
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"])
            .AddProcessAllocatedMemoryHealthCheck(maximumMegabytesAllocated: 512, name: "Process Allocated Memory", tags: ["memory"]);

        builder.Services.AddHostedService<LogConsumerService>();
        builder.Services.AddHostedService<EventProcessLogger>();
        builder.Services.AddHostedService<ExternalMessageLogConsumer>();

        // Viewership capture. The consumer turns the status API's connect/disconnect stream into
        // sessions; the reconciler closes the ones no end ever arrives for. Both belong here rather
        // than in the status API because exactly one of these pods runs per live event, so neither
        // needs a lock.
        builder.Services.AddSingleton<SimulationGate>();
        builder.Services.AddHostedService<ViewerSessionLogConsumer>();
        builder.Services.AddHostedService<ViewerSessionReconciler>();

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Logger";
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

        app.UseMetricServer();
        app.UseAuthorization();
        app.MapControllers();
        app.Run();
    }
}
