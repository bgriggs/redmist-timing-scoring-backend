using Asp.Versioning;
using HealthChecks.UI.Client;
using Keycloak.AuthServices.Authentication;
using Keycloak.AuthServices.Authorization;
using Keycloak.AuthServices.Common;
using MessagePack.AspNetCoreMvcFormatter;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared;
using RedMist.ControlLogs;
using RedMist.Database;
using StackExchange.Redis;
using System.Reflection;

namespace RedMist.EventManagement;

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

        //// Configure Rate Limiting - stricter limits for internal-facing admin API
        //builder.Services.AddRedMistRateLimiting(options =>
        //{
        //    options.SwaggerPermitLimit = 5; // Lower limit for admin API
        //    options.GlobalPermitLimit = 30;
        //});

        builder.Services.AddControllers(options =>
        {
            // Add MessagePack formatter
            options.InputFormatters.Add(new MessagePackInputFormatter(ContractlessStandardResolver.Options));
            options.OutputFormatters.Add(new MessagePackOutputFormatter(ContractlessStandardResolver.Options));
            options.FormatterMappings.SetMediaTypeMappingForFormat("msgpack", "application/x-msgpack");
        });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo 
            { 
                Title = "RedMist Event Management API", 
                Version = "v1",
                Description = "API for managing racing events, configurations, and organization settings",
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
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\""

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

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";
        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConn, c => { c.AbortOnConnectFail = false; c.ConnectRetry = 10; c.ConnectTimeout = 10; }));

        builder.Services.AddHealthChecks()
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"])
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddProcessAllocatedMemoryHealthCheck(maximumMegabytesAllocated: 400, name: "Process Allocated Memory", tags: new[] { "memory" });

        builder.Services.AddTransient<IControlLogFactory, ControlLogFactory>();

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

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "Event Management";
            app.UseDeveloperExceptionPage();
        }

        // Support for running behind a path-based proxy (e.g., /event-management)
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
            c.SwaggerEndpoint("v1/swagger.json", "RedMist Event Management API V1");
            c.RoutePrefix = "swagger";
            c.DocumentTitle = "RedMist Event Management API Documentation";
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

        await app.RunAsync();
    }
}
