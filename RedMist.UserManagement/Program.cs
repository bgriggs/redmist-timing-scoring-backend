using Asp.Versioning;
using HealthChecks.UI.Client;
using Keycloak.AuthServices.Authentication;
using Keycloak.AuthServices.Authorization;
using Keycloak.AuthServices.Common;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using System.Reflection;

namespace RedMist.UserManagement;

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

        //// Configure Rate Limiting - stricter limits for user management API
        //builder.Services.AddRedMistRateLimiting(options =>
        //{
        //    options.SwaggerPermitLimit = 5; // Lower limit for admin API
        //    options.GlobalPermitLimit = 30;
        //});

        builder.Services.AddControllers();

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
        
        // Configure Swagger only in Development
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo 
                { 
                    Title = "RedMist User Management API", 
                    Version = "v1",
                    Description = "API for managing users, organizations, and relay client provisioning",
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
                        }, 
                        Array.Empty<string>()
                    }
                });
            });
        }

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

        builder.Services.AddHealthChecks()
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"]);

        builder.Services.AddTransient<AssetsCdn>();

        var app = builder.Build();
        app.LogAssemblyInfo<Program>();

        // Configure the HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            Console.Title = "User Management";
        }

        // Support for running behind a path-based proxy (e.g., /user-management)
        var pathBase = app.Configuration["PathBase"];
        if (!string.IsNullOrEmpty(pathBase))
        {
            app.UsePathBase(pathBase);
        }

        //// Apply rate limiting middleware (must be after UsePathBase, before endpoints)
        //app.UseRateLimiter();

        // Enable Swagger only in Development
        if (app.Environment.IsDevelopment())
        {
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
                c.SwaggerEndpoint("v1/swagger.json", "RedMist User Management API V1");
                c.RoutePrefix = "swagger";
                c.DocumentTitle = "RedMist User Management API Documentation";
            });

            // Apply rate limiting to Swagger JSON endpoints (Development only)
            app.MapGet("/swagger/{documentName}/swagger.json", async (string documentName, HttpContext httpContext) =>
            {
                // Forward to the Swagger middleware
                await httpContext.Response.CompleteAsync();
            }).RequireRateLimiting("swagger");
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
