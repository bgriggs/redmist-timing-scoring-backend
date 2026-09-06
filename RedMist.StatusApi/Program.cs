using Asp.Versioning;
using HealthChecks.UI.Client;
using Keycloak.AuthServices.Authorization;
using Keycloak.AuthServices.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.OpenApi;
using NLog.Extensions.Logging;
using Npgsql;
using Prometheus;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Extensions;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Services;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.StatusApi.Services;
using RedMist.StatusApi.Services.Exports;
using StackExchange.Redis;
using System.IO.Compression;
using System.Reflection;
using System.Globalization;
using System.Threading.RateLimiting;

namespace RedMist.StatusApi;

public class Program
{
    /// <summary>Live session polling, limited per viewer instead of by the global limiter.</summary>
    internal const string SessionPollingPolicy = "current-session-polling";

    /// <summary>Sponsor telemetry and the sponsor list, limited on their own terms.</summary>
    internal const string SponsorTelemetryPolicy = "sponsor-telemetry";

    /// <summary>Export generation, limited on top of the global limiter rather than instead of it.</summary>
    internal const string ExportsPolicy = "exports";

    /// <summary>The cheaper "is there anything to export" probe, kept off the export allowance.</summary>
    internal const string ExportsAvailabilityPolicy = "exports-availability";

    /// <summary>
    /// Policies that stand in for the global limiter rather than stacking on top of it.
    /// </summary>
    /// <remarks>
    /// Polling is excluded so real-time updates are never queued behind the global limiter's queue,
    /// where they arrive out of step with the delta subscription. Sponsor telemetry is excluded
    /// because its own allowance is the looser of the two, so the global limiter would be the one
    /// doing the limiting. The export policies are deliberately absent: those tighten the global
    /// limiter rather than replacing it, and are meant to be limited twice.
    /// </remarks>
    private static readonly string[] policiesReplacingTheGlobalLimiter = [SessionPollingPolicy, SponsorTelemetryPolicy];

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
                    // AllowAnyHeader covers request headers only. Export downloads name their file in
                    // Content-Disposition, and the browser hides every response header from a
                    // cross-origin caller unless it is named here - the UI is served from a different
                    // origin than this API, so without this it silently falls back to a generated name.
                    .WithExposedHeaders("Content-Disposition")
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

        // Extract JWT from query string for SignalR WebSocket connections
        // (WebSocket API does not support custom HTTP headers, so the token is passed via ?access_token=)
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure(options =>
            {
                var existingOnMessageReceived = options.Events?.OnMessageReceived;
                options.Events ??= new JwtBearerEvents();
                options.Events.OnMessageReceived = async context =>
                {
                    if (existingOnMessageReceived != null)
                        await existingOnMessageReceived(context);

                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/event-status"))
                    {
                        context.Token = accessToken;
                    }
                };
            });

        // Rate limiting
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Say when to come back, and say here that we turned someone away. Without the first the
            // viewer app retries after 500ms into a bucket that refills more slowly than that, so a
            // rejection reliably becomes three; without the second a limiter rejecting most of the
            // traffic looks identical from the server to one rejecting none - the live event that
            // prompted all this was diagnosed entirely from client crash reports, because the
            // service itself recorded nothing.
            options.OnRejected = (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("RateLimiting");
                logger?.LogWarning("Rate limited {Method} {Path} for {Caller}",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path,
                    GetCallerKey(context.HttpContext));

                return ValueTask.CompletedTask;
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                if (IsExcludedFromGlobalLimiter(httpContext))
                {
                    return RateLimitPartition.GetNoLimiter("excluded-from-global-rate-limiter");
                }

                var clientIp = GetClientIp(httpContext);

                if (httpContext.User.Identity?.IsAuthenticated == true)
                {
                    // Subject *and* caller, not subject alone. The viewer apps all authenticate as
                    // the same service account, so the subject on its own is one bucket for the
                    // entire user base. Keeping it in the key still separates one client or service
                    // from another; adding the caller separates the callers sharing a subject. A
                    // service running several replicas gets a bucket per replica address rather
                    // than one between them, which is a loosening, and the right one: they are
                    // separate callers doing separate work.
                    var subject = httpContext.User.FindFirst("sub")?.Value
                        ?? httpContext.User.Identity?.Name
                        ?? clientIp;

                    return RateLimitPartition.GetTokenBucketLimiter(
                        $"authenticated:{subject}:{GetCallerKey(httpContext)}",
                        _ => new TokenBucketRateLimiterOptions
                        {
                            TokenLimit = 30,
                            ReplenishmentPeriod = TimeSpan.FromSeconds(0.25),
                            TokensPerPeriod = 1,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 10,
                            AutoReplenishment = true
                        });
                }

                return RateLimitPartition.GetTokenBucketLimiter(
                    $"anonymous:{clientIp}",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 30,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1.1),
                        TokensPerPeriod = 1,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 6,
                        AutoReplenishment = true
                    });
            });

            options.AddFixedWindowLimiter("swagger", config =>
            {
                config.PermitLimit = 3;
                config.Window = TimeSpan.FromSeconds(15);
                config.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                config.QueueLimit = 0;
            });

            // Live session polling, keyed by caller address. Every caller here is somebody watching
            // an event, so the address is the whole partition - authentication says nothing useful
            // about who is asking, and the subject claim actively misleads (see GetCallerKey).
            //
            // A viewer polls every five seconds and retries a failure up to three times with
            // backoff, so the shape that matters is a burst over a slow sustained rate: a fixed
            // window of one admits the poll and then rejects its own retries, turning a blip into a
            // rejection.
            options.AddPolicy(SessionPollingPolicy, GetSessionPollingPartition);

            options.AddPolicy(SponsorTelemetryPolicy, httpContext =>
            {
                var clientIp = GetClientIp(httpContext);

                return RateLimitPartition.GetTokenBucketLimiter(
                    $"sponsor-telemetry:{clientIp}",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 60,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(5),
                        TokensPerPeriod = 25,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 5,
                        AutoReplenishment = true
                    });
            });

            // Exports. A single request here reads a whole session and writes a file, so this is by
            // far the most expensive thing a caller can ask this service for. The bucket allows a
            // short burst - a user picking a format, changing their mind, and picking another - and
            // then refills slowly. Nothing queues: an export that has to wait its turn is one the
            // user has already given up on, and holding the request open costs a connection on a pod
            // whose real job is the live SignalR feed.
            options.AddPolicy(ExportsPolicy, httpContext =>
            {
                var partitionKey = httpContext.User.Identity?.IsAuthenticated == true
                    ? $"authenticated:{httpContext.User.FindFirst("sub")?.Value ?? httpContext.User.Identity?.Name ?? GetClientIp(httpContext)}"
                    : $"anonymous:{GetClientIp(httpContext)}";

                return RateLimitPartition.GetTokenBucketLimiter(
                    $"exports:{partitionKey}",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 5,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                        TokensPerPeriod = 1,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });

            // Asking whether an export is available is a handful of index lookups, nothing like
            // producing one. It gets its own bucket so that browsing sessions - the client calls this
            // on every session view - cannot spend the allowance the user needs a moment later for
            // the download they actually clicked.
            options.AddPolicy(ExportsAvailabilityPolicy, httpContext =>
            {
                var partitionKey = httpContext.User.Identity?.IsAuthenticated == true
                    ? $"authenticated:{httpContext.User.FindFirst("sub")?.Value ?? httpContext.User.Identity?.Name ?? GetClientIp(httpContext)}"
                    : $"anonymous:{GetClientIp(httpContext)}";

                return RateLimitPartition.GetTokenBucketLimiter(
                    $"exports-availability:{partitionKey}",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 20,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(2),
                        TokensPerPeriod = 1,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        // Caps concurrent export file generation across every request this replica is serving. The
        // rate limiter above bounds one caller; this bounds all of them at once, which is the number
        // that decides whether this pod stays inside its memory limit.
        builder.Services.AddSingleton<ExportConcurrencyLimiter>();

        // Bounds generated-but-not-yet-downloaded export files. The concurrency limiter releases its
        // slot when a file is written rather than when it is delivered, so without this the number of
        // large files sitting on the node disk is governed only by the rate limiter - which
        // partitions on a client IP taken from request headers a caller can vary at will.
        builder.Services.AddSingleton<StagedExportTracker>();

        // Sweeps export files a killed process left behind and proves the PDF renderer loads, so a
        // broken native dependency fails at deploy time rather than on the first request of a race.
        builder.Services.AddHostedService<ExportStartupService>();

        // Sponsor telemetry background queue
        builder.Services.AddSingleton<SponsorTelemetryQueue>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SponsorTelemetryQueue>());

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
        var npgsqlConnBuilder = new NpgsqlConnectionStringBuilder(sqlConn) { MinPoolSize = 3, MaxPoolSize = 10 };
        builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(npgsqlConnBuilder.ConnectionString));

        string redisConn = $"{builder.Configuration["REDIS_SVC"]},password={builder.Configuration["REDIS_PW"]}";

        // Configure Redis with settings for SignalR backplane in multi-replica environment
        var redisOptions = ConfigurationOptions.Parse(redisConn);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = 10;
        redisOptions.ConnectTimeout = 10000; // 10 seconds
        redisOptions.SyncTimeout = 10000;
        redisOptions.AsyncTimeout = 10000;
        redisOptions.KeepAlive = 60;
        redisOptions.ReconnectRetryPolicy = new ExponentialRetry(5000);

        var redisMux = ConnectionMultiplexer.Connect(redisOptions);
        builder.Services.AddSingleton<IConnectionMultiplexer>(redisMux);

        // Share Data Protection keys across replicas via Redis
        builder.Services.AddDataProtection()
            .SetApplicationName("RedMist-StatusApi")
            .PersistKeysToStackExchangeRedis(redisMux, "DataProtection-Keys-StatusApi");

        builder.Services.AddHealthChecks()
            .AddRedis(redisConn, tags: ["cache", "redis"])
            .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"]);

        builder.Services.AddRedMistSignalR(redisConn);
        builder.Services.AddSingleton<Controllers.V2.EventsController>();
        builder.Services.AddSingleton<IEventAccessValidator, EventAccessValidator>();

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

        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
            [
                "application/json",
                "application/x-msgpack"
            ]);
        });
        builder.Services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

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

        app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
        {
            var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            if (ex is NpgsqlException npgEx && npgEx.Message.Contains("connection pool", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "5";
                await ctx.Response.WriteAsync("Service temporarily unavailable. Please retry.");
            }
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }));

        app.UseResponseCompression();
        app.UseHttpsRedirection();
        app.UseCors();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseMetricServer();
        app.MapControllers();
        app.MapHub<StatusHub>("/event-status", options =>
        {
            // WebSockets only: a single persistent connection that works across replicas
            // without sticky sessions. Long Polling/SSE require sticky sessions which
            // don't work reliably cross-origin (third-party cookie restrictions).
            options.Transports = HttpTransportType.WebSockets;
        }).RequireCors();
        app.Run();
    }

    /// <summary>
    /// Identifies the caller for rate limiting.
    /// </summary>
    /// <remarks>
    /// Deliberately not the <c>sub</c> claim. The viewer apps authenticate with client credentials
    /// (<c>grant_type=client_credentials</c> against a client id and secret baked into the app), so
    /// every install presents the same service-account subject. Keying on it collapses the entire
    /// user base into one bucket, which is what turned a live event into a wall of 429s.
    ///
    /// Equally deliberately, nothing here comes from a header the caller controls. A per-install id
    /// would separate viewers behind one NAT, which the address cannot, but this endpoint is
    /// anonymous, uncached and excluded from the global limiter, so its own limiter is the only
    /// thing in front of it - and a key a caller can rotate at will is a limiter that can be turned
    /// off by anyone who thinks to. The address arrives from Cloudflare as CF-Connecting-IP, which
    /// the edge overwrites, so it cannot be forged from the internet; see the load test's README,
    /// where that is what defeats its own attempt to spoof its way out of this limit.
    ///
    /// The cost is that viewers behind one NAT share a bucket, so the bucket is sized for a group
    /// rather than a device.
    /// </remarks>
    internal static string GetCallerKey(HttpContext httpContext) => $"ip:{GetClientIp(httpContext)}";

    /// <summary>
    /// Whether this request is handled by a policy that replaces the global limiter.
    /// </summary>
    /// <remarks>
    /// Asks the endpoint what policy it declares rather than matching its path. The paths are
    /// versioned through a route constraint - <c>v{version:apiVersion}</c> - and the actions are
    /// reachable by more spellings than the obvious one: the legacy unversioned route, the
    /// versioned route, and whichever forms of the version segment the constraint accepts. A list
    /// of literal paths has to be kept in step with all of that, and silently stops excluding
    /// anything it falls behind on - at which point real-time polling lands in the global limiter's
    /// queue, which is the one thing the exclusion exists to prevent.
    ///
    /// The endpoint has been resolved by the time this runs, and that is load-bearing: routing is
    /// inserted at the head of the pipeline precisely because nothing calls <c>UseRouting</c>
    /// explicitly, and the limiter is added well after it. Adding an explicit <c>UseRouting</c>
    /// after <c>UseRateLimiter</c> would suppress that insertion, leave no endpoint to read here,
    /// and drop every polling request into the queue this exists to keep it out of - without
    /// failing anything.
    ///
    /// Reads the policy by name, so an endpoint given a policy object rather than a name - the
    /// <c>RequireRateLimiting(IRateLimiterPolicy)</c> overload, which leaves the name null - would
    /// not be recognized here. Nothing uses that overload; if something does, name it instead.
    /// </remarks>
    internal static bool IsExcludedFromGlobalLimiter(HttpContext httpContext)
    {
        var endpoint = httpContext.GetEndpoint();

        // The status hub is mapped at a literal path and carries no policy of its own, so it is
        // named here; nothing versions it, which is why a path is the right test for this one. It
        // still has to have routed somewhere: without that check, anything under /event-status that
        // is on its way to a 404 escapes the limiter for the asking.
        if (endpoint is not null && httpContext.Request.Path.StartsWithSegments("/event-status"))
        {
            return true;
        }

        // The nearest attribute wins, so an action naming its own policy overrides its controller's.
        var policy = endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        return policy is not null && policiesReplacingTheGlobalLimiter.Contains(policy);
    }

    /// <summary>
    /// The rate limit partition for live session polling, as the policy registers it.
    /// </summary>
    /// <remarks>
    /// Separate from its registration so it can be tested: what broke in production was the key
    /// this produces, and a test that reaches it through the policy is the one that would have
    /// caught it.
    /// </remarks>
    internal static RateLimitPartition<string> GetSessionPollingPartition(HttpContext httpContext)
    {
        return RateLimitPartition.GetTokenBucketLimiter(
            $"session-polling:{GetCallerKey(httpContext)}",
            _ => new TokenBucketRateLimiterOptions
            {
                // Sized for a group sharing an address, not for one device. A viewer polls every
                // five seconds - a fifth of a request a second - so this carries roughly fifteen of
                // them, and the burst absorbs a handful starting or resuming at once, or one
                // working through its three retries.
                TokenLimit = 15,
                TokensPerPeriod = 3,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                // Nothing waits: a queued session state is stale by the time it is served, and
                // holding the request costs a connection on a pod whose real job is the live
                // SignalR feed.
                QueueLimit = 0,
            });
    }

    private static string GetClientIp(HttpContext httpContext)
    {
        var remoteIpAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var xForwardedFor = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        var cfConnectingIp = httpContext.Request.Headers["CF-Connecting-IP"].ToString();
        var forwardedForIp = string.IsNullOrWhiteSpace(xForwardedFor)
            ? null
            : xForwardedFor.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return !string.IsNullOrWhiteSpace(cfConnectingIp)
            ? cfConnectingIp
            : !string.IsNullOrWhiteSpace(forwardedForIp)
                ? forwardedForIp
                : remoteIpAddress;
    }
}
