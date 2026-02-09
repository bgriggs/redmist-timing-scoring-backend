using BigMission.TestHelpers;
using HealthChecks.UI.Client;
using Keycloak.AuthServices.Authentication;
using Keycloak.AuthServices.Authorization;
using Keycloak.AuthServices.Common;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.OpenApi;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Services;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.DriverInformation;
using RedMist.EventProcessor.EventStatus.FlagData;
using RedMist.EventProcessor.EventStatus.InCarDriverMode;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.Multiloop;
using RedMist.EventProcessor.EventStatus.PenaltyEnricher;
using RedMist.EventProcessor.EventStatus.PipelineBlocks;
using RedMist.EventProcessor.EventStatus.PositionEnricher;
using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.EventStatus.SessionMonitoring;
using RedMist.EventProcessor.EventStatus.Video;
using RedMist.EventProcessor.EventStatus.X2;
using StackExchange.Redis;
using System.Reflection;

namespace RedMist.EventProcessor;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin();
                policy.AllowAnyHeader();
            });
        });
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo 
            { 
                Title = "RedMist Timing and Scoring Service", 
                Version = "v1",
                Description = "Internal service for real-time event processing and timing calculations",
                Contact = new OpenApiContact
                {
                    Name = "Red Mist Timing & Scoring",
                    Url = new Uri("https://github.com/bgriggs/redmist-timing-scoring-backend")
                }
            });

            // Include XML comments
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath))
            {
                c.IncludeXmlComments(xmlPath);
            }

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme()
            {
                Name = "Authorization",
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT Authorization header using the Bearer scheme."

            });
            c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecuritySchemeReference("Bearer"), []
                }
            });
        });

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";

        // Configure Redis with consistent settings matching StatusApi for reliable multi-replica operation
        var redisOptions = ConfigurationOptions.Parse(redisConn);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = 10;
        redisOptions.ConnectTimeout = 10000; // 10 seconds
        redisOptions.SyncTimeout = 10000;
        redisOptions.AsyncTimeout = 10000;
        redisOptions.KeepAlive = 60;
        redisOptions.ReconnectRetryPolicy = new ExponentialRetry(5000);

        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisOptions));
        builder.Services.AddHybridCache(o => o.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(7), LocalCacheExpiration = TimeSpan.FromDays(7) });
        builder.Services.AddSingleton<SessionContext>();
        builder.Services.AddSingleton<MultiloopProcessor>();
        builder.Services.AddSingleton<RMonitorDataProcessor>();
        builder.Services.AddSingleton<PitProcessor>();
        builder.Services.AddSingleton<FlagProcessor>();
        builder.Services.AddSingleton<PositionDataEnricher>();
        builder.Services.AddSingleton<ResetProcessor>();
        builder.Services.AddSingleton<CarLapHistoryService>();
        builder.Services.AddSingleton<LapProcessor>();
        builder.Services.AddSingleton<VideoEnricher>();
        builder.Services.AddSingleton<FastestPaceEnricher>();
        builder.Services.AddSingleton<ProjectedLapTimeEnricher>();
        builder.Services.AddSingleton<DriverEnricher>();
        builder.Services.AddSingleton<UpdateConsolidator>();
        builder.Services.AddSingleton<StatusAggregator>();
        builder.Services.AddSingleton<StartingPositionProcessor>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<StartingPositionProcessor>());
        builder.Services.AddSingleton<ControlLogEnricher>();
        builder.Services.AddSingleton<DriverModeProcessor>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ControlLogEnricher>());
        builder.Services.AddSingleton<SessionMonitor>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SessionMonitor>());
        builder.Services.AddSingleton<SessionStateProcessingPipeline>();
        builder.Services.AddHostedService<EventAggregatorService>();
        builder.Services.AddHostedService<ConsistencyCheckService>();
        builder.Services.AddMediatorFromAssemblyContaining<Program>();

        builder.Services.AddHealthChecks()
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"])
            .AddRedis(redisConn, tags: ["cache", "redis"]);

        builder.Services.AddRedMistSignalR(redisConn);

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Event Processor";
        }

        // Enable Swagger in all environments
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "RedMist Timing and Scoring Service V1");
            c.RoutePrefix = "swagger";
            c.DocumentTitle = "RedMist Timing and Scoring Service Documentation";
        });

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

        app.UseHttpsRedirection();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.RunAsync();
    }
}
