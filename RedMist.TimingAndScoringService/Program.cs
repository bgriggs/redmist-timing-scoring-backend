using BigMission.TestHelpers;
using HealthChecks.UI.Client;
using Keycloak.AuthServices.Authentication;
using Keycloak.AuthServices.Authorization;
using Keycloak.AuthServices.Common;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.OpenApi.Models;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Services;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.FlagData;
using RedMist.TimingAndScoringService.EventStatus.InCarDriverMode;
using RedMist.TimingAndScoringService.EventStatus.LapData;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.PenaltyEnricher;
using RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;
using RedMist.TimingAndScoringService.EventStatus.PositionEnricher;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.SessionMonitoring;
using RedMist.TimingAndScoringService.EventStatus.X2;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService;

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
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "Timing and Scoring Services", Version = "v1" });
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme()
            {
                Name = "Authorization",
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT Authorization header using the Bearer scheme."

            });
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                      new OpenApiSecurityScheme
                      {
                          Reference = new OpenApiReference
                          {
                              Type = ReferenceType.SecurityScheme,
                              Id = "Bearer"
                          }
                      }, []
                }
            });
        });

        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseSqlServer(sqlConn));

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";
        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn, c => { c.AbortOnConnectFail = false; c.ConnectRetry = 5; c.ConnectTimeout = 10; }));
        builder.Services.AddHybridCache(o => o.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(100), LocalCacheExpiration = TimeSpan.FromDays(100) });
        builder.Services.AddSingleton<SessionContext>();
        builder.Services.AddSingleton<MultiloopProcessor>();
        builder.Services.AddSingleton<RMonitorDataProcessorV2>();
        builder.Services.AddSingleton<PitProcessorV2>();
        builder.Services.AddSingleton<FlagProcessorV2>();
        builder.Services.AddSingleton<PositionDataEnricher>();
        builder.Services.AddSingleton<ResetProcessor>();
        builder.Services.AddSingleton<LapProcessor>();
        builder.Services.AddSingleton<UpdateConsolidator>();
        builder.Services.AddSingleton<StatusAggregatorV2>();
        builder.Services.AddSingleton<StartingPositionProcessor>();
        builder.Services.AddSingleton<ControlLogEnricher>();
        builder.Services.AddSingleton<DriverModeProcessor>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ControlLogEnricher>());
        builder.Services.AddSingleton<SessionMonitorV2>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SessionMonitorV2>());
        builder.Services.AddSingleton<SessionStateProcessingPipeline>();
        builder.Services.AddHostedService<EventAggregatorService>();
        builder.Services.AddHostedService<ConsistencyCheckService>();
        builder.Services.AddMediatorFromAssemblyContaining<Program>();

        builder.Services.AddHealthChecks()
            .AddSqlServer(sqlConn, tags: ["db", "sql", "sqlserver"])
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddProcessAllocatedMemoryHealthCheck(maximumMegabytesAllocated: 7200, name: "Process Allocated Memory", tags: ["memory"]);

        builder.Services.AddRedMistSignalR(redisConn);

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Event Processor";
            app.UseDeveloperExceptionPage();
            app.UseSwagger();
            app.UseSwaggerUI();
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

        app.UseHttpsRedirection();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.RunAsync();
    }
}
