using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NLog.Extensions.Logging;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.ControlLogProcessor.Services;
using RedMist.ControlLogs;
using RedMist.ControlLogs.Announcements;
using RedMist.Database;
using StackExchange.Redis;

namespace RedMist.ControlLogProcessor;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        builder.Services.AddControllers();
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        string sqlConn = builder.Configuration["ConnectionStrings:Default"]
            ?? throw new ArgumentNullException(nameof(builder.Configuration), "SQL Connection is missing.");
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";
        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn, c => { c.AbortOnConnectFail = false; c.ConnectRetry = 10; c.ConnectTimeout = 10; }));

        builder.Services.AddHealthChecks()
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"]);

        // Shared between the announcement stream consumer (writer) and the control log poll (reader,
        // via ControlLogFactory -> AnnouncementControlLog), so it must be a singleton.
        builder.Services.AddSingleton<IAnnouncementControlLogStore, AnnouncementControlLogStore>();
        builder.Services.AddTransient<IControlLogFactory, ControlLogFactory>();
        // Used by SessionStateAnnouncementSyncService to call the event processor's /status/GetStatus.
        builder.Services.AddHttpClient("EventProcessor", c => c.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.AddHostedService<StatusAggregatorService>();
        builder.Services.AddHostedService<AnnouncementStreamConsumerService>();
        builder.Services.AddHostedService<SessionStateAnnouncementSyncService>();

        builder.Services.AddRedMistSignalR(redisConn);

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Control Log Processor";
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
