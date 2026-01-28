using Asp.Versioning;
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
using RedMist.Backend.Shared.Extensions;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.StatusApi.Services;
using StackExchange.Redis;
using System.Reflection;

namespace RedMist.StatusApi;

public class Program
{
    private static readonly string[] setupAction =
    [
        "RedMist.Backend.Shared.xml",
        "RedMist.Database.xml",
        "RedMist.TimingCommon.xml"
    ];

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddNLog("NLog");

        builder.Services.AddCors(options =>
        {
            // Unified policy supporting both 3rd party SignalR clients and sticky sessions
            options.AddDefaultPolicy(policy =>
            {
                policy.SetIsOriginAllowed(_ => true) // Allow any origin (for 3rd party integrations)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials(); // Enable credentials for sticky session cookies (required for multi-replica SignalR)
            });
        });

        // Add services to the container.
        builder.Services.AddKeycloakWebApiAuthentication(builder.Configuration);
        builder.Services.AddAuthorization().AddKeycloakAuthorization(options =>
        {
            options.EnableRolesMapping = RolesClaimTransformationSource.Realm;
            // Note, this should correspond to role configured with KeycloakAuthenticationOptions
            options.RoleClaimType = KeycloakConstants.RoleClaimType;
        });

        //// Configure Rate Limiting with default settings for Swagger
        //builder.Services.AddRedMistRateLimiting(options =>
        //{
        //    options.SwaggerPermitLimit = 5;
        //    options.GlobalPermitLimit = 30;
        //});

        builder.Services.AddControllersWithMessagePack();

        // Configure Swagger/OpenAPI with XML comments
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo 
            { 
                Title = "RedMist Status API", 
                Version = "v1",
                Description = "API for retrieving real-time event status, timing data, and race information",
                Contact = new OpenApiContact
                {
                    Name = "Red Mist Timing & Scoring",
                    Url = new Uri("https://github.com/bgriggs/redmist-timing-scoring-backend")
                }
            });
            
            c.SwaggerDoc("v2", new OpenApiInfo 
            { 
                Title = "RedMist Status API", 
                Version = "v2",
                Description = "Enhanced API with improved data models for event status and timing data"
            });

            // Include XML comments
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath))
            {
                c.IncludeXmlComments(xmlPath);
            }

            var modelXmlFiles = setupAction;

            foreach (var modelXmlFile in modelXmlFiles)
            {
                var modelXmlPath = Path.Combine(AppContext.BaseDirectory, modelXmlFile);
                if (File.Exists(modelXmlPath))
                {
                    c.IncludeXmlComments(modelXmlPath);
                }
            }

            // Add security definition for Bearer token
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });

            c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecuritySchemeReference("Bearer"),
                    new List<string>()
                }
            });
        });

        builder.Services.AddHybridCache(o => o.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(4), LocalCacheExpiration = TimeSpan.FromMinutes(5) });

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";

        // Configure Redis with robust settings for SignalR backplane in multi-replica environment
        var redisOptions = ConfigurationOptions.Parse(redisConn);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = 10;
        redisOptions.ConnectTimeout = 10000; // 10 seconds
        redisOptions.SyncTimeout = 10000;
        redisOptions.AsyncTimeout = 10000;
        redisOptions.KeepAlive = 60;
        redisOptions.ReconnectRetryPolicy = new ExponentialRetry(5000);

        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisOptions));

        builder.Services.AddHealthChecks()
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"]);

        builder.Services.AddRedMistSignalR(redisConn);
        builder.Services.AddSingleton<Controllers.V2.EventsController>();

        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient("EventProcessor", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "RedMist-StatusApi/1.0");
            client.DefaultRequestHeaders.ConnectionClose = false;
            client.DefaultRequestHeaders.Add("Keep-Alive", "timeout=300, max=100");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler()
        {
            MaxConnectionsPerServer = 20,
            PooledConnectionLifetime = TimeSpan.FromMinutes(30), 
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5), 
            ResponseDrainTimeout = TimeSpan.FromSeconds(2),
            EnableMultipleHttp2Connections = true
        })
        .ConfigureHttpClient((serviceProvider, client) =>
        {
            
        });

        // Configure API Versioning
        builder.Services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        })
        .AddMvc()
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        builder.Services.AddHostedService<MetricsPublisher>();

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Status API";
        }

        // Support for running behind a path-based proxy (e.g., /status)
        // This allows Swagger to work correctly when accessed via /status/swagger
        var pathBase = app.Configuration["PathBase"];
        if (!string.IsNullOrEmpty(pathBase))
        {
            app.UsePathBase(pathBase);
        }

        //// Apply rate limiting middleware (must be after UsePathBase, before endpoints)
        //app.UseRateLimiter();

        // Enable Swagger in all environments
        app.UseSwagger(c =>
        {
            c.PreSerializeFilters.Add((swagger, httpReq) =>
            {
                // Ensure swagger knows about the path base for proper URL generation
                if (!string.IsNullOrEmpty(pathBase))
                {
                    swagger.Servers =
                    [
                        new() { Url = $"{httpReq.Scheme}://{httpReq.Host.Value}{pathBase}" }
                    ];
                }
            });
        });
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("v2/swagger.json", "RedMist Status API V2");
            c.SwaggerEndpoint("v1/swagger.json", "RedMist Status API V1");
            c.RoutePrefix = "swagger";
            c.DocumentTitle = "RedMist Status API Documentation";
        });

        // Apply rate limiting to Swagger JSON endpoints
        app.MapGet("/swagger/{documentName}/swagger.json", async (string documentName, HttpContext httpContext) =>
        {
            // Forward to the Swagger middleware
            await httpContext.Response.CompleteAsync();
        }).RequireRateLimiting("swagger");

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
        app.MapHub<StatusHub>("/event-status").RequireCors();
        app.Run();
    }
}
