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
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using RedMist.TimingAndScoringService.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.Hubs;
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

        builder.Services.AddKeycloakWebApiAuthentication(builder.Configuration);
        builder.Services.AddAuthorization().AddKeycloakAuthorization(options =>
        {
            options.EnableRolesMapping = RolesClaimTransformationSource.Realm;
            // Note, this should correspond to role configured with KeycloakAuthenticationOptions
            options.RoleClaimType = KeycloakConstants.RoleClaimType;
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
        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn, c => { c.AbortOnConnectFail = false; c.ConnectRetry = 10; c.ConnectTimeout = 10; }));

        builder.Services.AddHostedService(s => s.GetRequiredService<EventDistribution>());
#pragma warning disable EXTEXP0018
        builder.Services.AddHybridCache(o => o.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(100), LocalCacheExpiration = TimeSpan.FromDays(100) });
#pragma warning restore EXTEXP0018
        builder.Services.AddSingleton<EventDistribution>();
        builder.Services.AddSingleton<IDateTimeHelper, DateTimeHelper>();
        builder.Services.AddSingleton<IDistributedLockFactory>(r => RedLockFactory.Create([new RedLockMultiplexer(r.GetRequiredService<IConnectionMultiplexer>())]));
        builder.Services.AddTransient<IDataProcessorFactory, DataProcessorFactory>();
        builder.Services.AddHostedService<EventAggregator>();
        builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

        builder.Services.AddHealthChecks()
            //.AddCheck<StartupHealthCheck>("Startup", tags: ["startup"])
            .AddSqlServer(sqlConn, tags: ["db", "sql", "sqlserver"])
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddProcessAllocatedMemoryHealthCheck(maximumMegabytesAllocated: 1024, name: "Process Allocated Memory", tags: ["memory"]);

        builder.Services.AddSignalR(o => o.MaximumParallelInvocationsPerClient = 3)
            .AddStackExchangeRedis(redisConn, options =>
            {
                options.Configuration.ChannelPrefix = RedisChannel.Literal("timing-scoring");
            });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Timing and Scoring Services";
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

        app.MapHub<TimingAndScoringHub>("/ts-hub");

        await app.RunAsync();
    }
}
