using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NLog.Extensions.Logging;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.ExternalDataCollection.Clients;
using RedMist.ExternalDataCollection.Services;
using StackExchange.Redis;

namespace RedMist.ExternalDataCollection;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        builder.Services.AddAuthorization();

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException(nameof(sqlConn));
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";
        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn, c => { c.AbortOnConnectFail = false; c.ConnectRetry = 10; c.ConnectTimeout = 10; }));

        builder.Services.AddHealthChecks()
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"])
            .AddProcessAllocatedMemoryHealthCheck(maximumMegabytesAllocated: 150, name: "Process Allocated Memory", tags: ["memory"]);

        builder.Services.AddSingleton<EventsChecker>();
        builder.Services.AddRedMistSignalR(redisConn);
        builder.Services.AddHostedService<SentinelStatusService>();
        builder.Services.AddTransient<SentinelClient>();
        builder.Services.AddSingleton<ExternalTelemetryClient>();

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "External Data Collection";
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
        app.Run();
    }
}
