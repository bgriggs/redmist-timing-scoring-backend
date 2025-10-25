using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;

namespace RedMist.Backend.Shared.Extensions;

/// <summary>
/// Extension methods for configuring rate limiting in RedMist services.
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Adds RedMist standard rate limiting policies for API services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional action to customize rate limiting options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedMistRateLimiting(
        this IServiceCollection services, 
        Action<RedMistRateLimitOptions>? configureOptions = null)
    {
        var options = new RedMistRateLimitOptions();
        configureOptions?.Invoke(options);

        services.AddRateLimiter(rateLimiterOptions =>
        {
            // Policy for Swagger UI endpoints - stricter limits for public documentation
            if (options.EnableSwaggerPolicy)
            {
                rateLimiterOptions.AddFixedWindowLimiter("swagger", config =>
                {
                    config.PermitLimit = options.SwaggerPermitLimit;
                    config.Window = TimeSpan.FromMinutes(options.SwaggerWindowMinutes);
                    config.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    config.QueueLimit = options.SwaggerQueueLimit;
                });
            }

            // Policy for API endpoints - more generous for authenticated users
            if (options.EnableApiPolicy)
            {
                rateLimiterOptions.AddTokenBucketLimiter("api", config =>
                {
                    config.TokenLimit = options.ApiTokenLimit;
                    config.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    config.QueueLimit = options.ApiQueueLimit;
                    config.ReplenishmentPeriod = TimeSpan.FromSeconds(options.ApiReplenishmentSeconds);
                    config.TokensPerPeriod = options.ApiTokensPerPeriod;
                    config.AutoReplenishment = true;
                });
            }

            // Global fallback rate limiter (partition by IP address)
            if (options.EnableGlobalLimiter)
            {
                rateLimiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    // Skip rate limiting for health check endpoints
                    if (httpContext.Request.Path.StartsWithSegments("/healthz"))
                    {
                        return RateLimitPartition.GetNoLimiter<string>("healthz");
                    }

                    // Allow custom exemptions
                    foreach (var exemptPath in options.ExemptPaths)
                    {
                        if (httpContext.Request.Path.StartsWithSegments(exemptPath))
                        {
                            return RateLimitPartition.GetNoLimiter<string>(exemptPath);
                        }
                    }

                    var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(ipAddress, partition => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.GlobalPermitLimit,
                        Window = TimeSpan.FromMinutes(options.GlobalWindowMinutes),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = options.GlobalQueueLimit
                    });
                });
            }

            // Configure rejection response
            rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiterOptions.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/json";
                
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
                    ? retryAfterValue.TotalSeconds.ToString("F0")
                    : "60";

                context.HttpContext.Response.Headers.RetryAfter = retryAfter;

                var response = new
                {
                    error = "Rate limit exceeded",
                    message = $"Too many requests. Please retry after {retryAfter} seconds.",
                    retryAfter = int.Parse(retryAfter)
                };

                await context.HttpContext.Response.WriteAsync(
                    System.Text.Json.JsonSerializer.Serialize(response),
                    cancellationToken);
            };
        });

        return services;
    }
}

/// <summary>
/// Configuration options for RedMist rate limiting.
/// </summary>
public class RedMistRateLimitOptions
{
    /// <summary>
    /// Enable the Swagger rate limiting policy. Default: true.
    /// </summary>
    public bool EnableSwaggerPolicy { get; set; } = true;

    /// <summary>
    /// Enable the API rate limiting policy. Default: false (opt-in).
    /// </summary>
    public bool EnableApiPolicy { get; set; } = false;

    /// <summary>
    /// Enable the global IP-based rate limiter. Default: true.
    /// </summary>
    public bool EnableGlobalLimiter { get; set; } = true;

    // Swagger Policy Settings
    /// <summary>
    /// Number of requests allowed per window for Swagger endpoints. Default: 10.
    /// </summary>
    public int SwaggerPermitLimit { get; set; } = 10;

    /// <summary>
    /// Window duration in minutes for Swagger policy. Default: 1.
    /// </summary>
    public int SwaggerWindowMinutes { get; set; } = 1;

    /// <summary>
    /// Queue limit for Swagger requests. Default: 5.
    /// </summary>
    public int SwaggerQueueLimit { get; set; } = 5;

    // API Policy Settings
    /// <summary>
    /// Token bucket size for API endpoints. Default: 100.
    /// </summary>
    public int ApiTokenLimit { get; set; } = 100;

    /// <summary>
    /// Number of tokens added per replenishment period. Default: 20.
    /// </summary>
    public int ApiTokensPerPeriod { get; set; } = 20;

    /// <summary>
    /// Replenishment period in seconds for API policy. Default: 10.
    /// </summary>
    public int ApiReplenishmentSeconds { get; set; } = 10;

    /// <summary>
    /// Queue limit for API requests. Default: 10.
    /// </summary>
    public int ApiQueueLimit { get; set; } = 10;

    // Global Limiter Settings
    /// <summary>
    /// Number of requests allowed per window per IP globally. Default: 50.
    /// </summary>
    public int GlobalPermitLimit { get; set; } = 50;

    /// <summary>
    /// Window duration in minutes for global limiter. Default: 1.
    /// </summary>
    public int GlobalWindowMinutes { get; set; } = 1;

    /// <summary>
    /// Queue limit for global limiter. Default: 5.
    /// </summary>
    public int GlobalQueueLimit { get; set; } = 5;

    /// <summary>
    /// Additional paths to exempt from rate limiting (e.g., "/metrics", "/favicon.ico").
    /// Health checks (/healthz) are always exempted.
    /// </summary>
    public List<string> ExemptPaths { get; set; } = [];
}
