namespace RedMist.EventManagement;

/// <summary>
/// Cross-origin access for this service.
/// </summary>
/// <remarks>
/// Extracted from Program so it can be asserted rather than assumed. This API was reached only by
/// desktop and server callers until the landing UI's admin page, and none of those enforce CORS, so
/// the policy went a long time without the one thing a browser needs from it.
/// </remarks>
public static class CorsSetup
{
    public static IServiceCollection AddRedMistCors(this IServiceCollection services)
    {
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin();
                policy.AllowAnyHeader();

                // Required, not decorative. A bearer token makes every request preflighted, and a
                // policy that names no methods answers the preflight without an
                // Access-Control-Allow-Methods header. A browser treats that as fatal only for a
                // method outside the CORS-safelisted three, so reads and POSTs went through and the
                // two PUT endpoints did not -- a partial failure that reads like a bad route rather
                // than a policy. StatusApi and UserManagement both allow any method; this one did not.
                policy.AllowAnyMethod();

                // Chrome caches a preflight for five seconds by default, so without this every action
                // on the review page costs two round trips to a cluster the reviewer is not near.
                policy.SetPreflightMaxAge(TimeSpan.FromMinutes(10));
            });
        });

        return services;
    }
}
