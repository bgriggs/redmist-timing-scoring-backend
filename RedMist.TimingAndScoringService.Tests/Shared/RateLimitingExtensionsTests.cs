using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedMist.Backend.Shared.Extensions;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The public API sits behind these limits, so what matters is that they partition per caller,
/// that the exemptions really exempt (a rate-limited health probe takes a pod out of rotation), and
/// that a rejected caller is told when to come back.
/// </summary>
[TestClass]
public class RateLimitingExtensionsTests
{
    /// <summary>
    /// Global limiters are built inside the configure delegate, so nothing owns them. Each one starts
    /// an idle-partition cleanup timer that would otherwise outlive the test for the whole run.
    /// </summary>
    private readonly List<PartitionedRateLimiter<HttpContext>> limiters = [];

    [TestCleanup]
    public async Task DisposeLimiters()
    {
        foreach (var limiter in limiters)
        {
            await limiter.DisposeAsync();
        }
    }

    private RateLimiterOptions BuildOptions(Action<RedMistRateLimitOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddRedMistRateLimiting(configure);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        if (options.GlobalLimiter != null)
        {
            limiters.Add(options.GlobalLimiter);
        }
        return options;
    }

    private static DefaultHttpContext RequestFrom(string? ipAddress, string path = "/api/events")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = ipAddress == null ? null : IPAddress.Parse(ipAddress);
        return context;
    }

    /// <summary>Attempts <paramref name="attempts"/> acquisitions and returns how many succeeded.</summary>
    private static int AcquireCount(PartitionedRateLimiter<HttpContext> limiter, HttpContext context, int attempts)
    {
        var acquired = 0;
        var leases = new List<RateLimitLease>();
        for (var i = 0; i < attempts; i++)
        {
            var lease = limiter.AttemptAcquire(context);
            leases.Add(lease);
            if (lease.IsAcquired)
            {
                acquired++;
            }
        }
        // Released only after the whole burst so a fixed window sees them as concurrent traffic.
        leases.ForEach(l => l.Dispose());
        return acquired;
    }

    #region Global limiter

    [TestMethod]
    public void GlobalLimiter_LimitsEachCallerIndependently()
    {
        var options = BuildOptions(o => o.GlobalPermitLimit = 2);
        var limiter = options.GlobalLimiter!;

        Assert.AreEqual(2, AcquireCount(limiter, RequestFrom("10.0.0.1"), 5), "the third request from a caller is over the limit");
        Assert.AreEqual(2, AcquireCount(limiter, RequestFrom("10.0.0.2"), 5), "a different caller gets its own window");
    }

    /// <summary>
    /// Kubernetes probes come from the node, not a user, and share a source address with everything
    /// else on it. Rate limiting them would restart healthy pods.
    /// </summary>
    [TestMethod]
    public void GlobalLimiter_NeverLimitsHealthChecks()
    {
        var options = BuildOptions(o => o.GlobalPermitLimit = 2);
        var limiter = options.GlobalLimiter!;

        Assert.AreEqual(20, AcquireCount(limiter, RequestFrom("10.0.0.1", "/healthz/ready"), 20));
    }

    [TestMethod]
    public void GlobalLimiter_NeverLimitsConfiguredExemptPaths()
    {
        var options = BuildOptions(o =>
        {
            o.GlobalPermitLimit = 2;
            o.ExemptPaths = ["/metrics"];
        });
        var limiter = options.GlobalLimiter!;

        Assert.AreEqual(20, AcquireCount(limiter, RequestFrom("10.0.0.1", "/metrics"), 20));
        Assert.AreEqual(2, AcquireCount(limiter, RequestFrom("10.0.0.1", "/api/events"), 20),
            "exempting a path must not exempt the caller");
    }

    /// <summary>
    /// Callers whose address the server cannot see all share one bucket, which is the safe direction
    /// to get it wrong in.
    /// </summary>
    [TestMethod]
    public void GlobalLimiter_TreatsCallersWithNoAddressAsOne()
    {
        var options = BuildOptions(o => o.GlobalPermitLimit = 2);
        var limiter = options.GlobalLimiter!;

        Assert.AreEqual(2, AcquireCount(limiter, RequestFrom(null), 5));
        Assert.AreEqual(0, AcquireCount(limiter, RequestFrom(null), 5), "the shared window is already exhausted");
    }

    [TestMethod]
    public void AddRedMistRateLimiting_WithTheGlobalLimiterDisabled_LeavesItUnconfigured()
    {
        Assert.IsNull(BuildOptions(o => o.EnableGlobalLimiter = false).GlobalLimiter);
    }

    #endregion

    #region Policies

    /// <summary>
    /// There is no public way to read the policy map back, but registering a duplicate policy name
    /// is an error, which makes it an accurate probe for whether a name is already taken.
    /// </summary>
    private static bool IsPolicyRegistered(RateLimiterOptions options, string policyName)
    {
        try
        {
            options.AddFixedWindowLimiter(policyName, config => config.PermitLimit = 1);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [TestMethod]
    public void AddRedMistRateLimiting_ByDefault_RegistersSwaggerButNotTheApiPolicy()
    {
        var options = BuildOptions();

        Assert.IsTrue(IsPolicyRegistered(options, "swagger"));
        Assert.IsFalse(IsPolicyRegistered(options, "api"), "the api policy is opt-in");
    }

    [TestMethod]
    public void AddRedMistRateLimiting_RegistersOnlyThePoliciesThatAreEnabled()
    {
        var options = BuildOptions(o =>
        {
            o.EnableSwaggerPolicy = false;
            o.EnableApiPolicy = true;
        });

        Assert.IsFalse(IsPolicyRegistered(options, "swagger"));
        Assert.IsTrue(IsPolicyRegistered(options, "api"));
    }

    #endregion

    #region Rejection response

    private sealed class FakeLease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter.Value;
                return true;
            }
            metadata = null;
            return false;
        }
    }

    private static async Task<(HttpContext Context, JsonElement Body)> RejectAsync(RateLimiterOptions options, TimeSpan? retryAfter)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = new FakeLease(retryAfter) }, CancellationToken.None);

        body.Position = 0;
        return (context, JsonDocument.Parse(body).RootElement);
    }

    [TestMethod]
    public void AddRedMistRateLimiting_RejectsWithTooManyRequests()
    {
        Assert.AreEqual(StatusCodes.Status429TooManyRequests, BuildOptions().RejectionStatusCode);
    }

    [TestMethod]
    public async Task OnRejected_ReportsTheRetryAfterTheLimiterSupplied()
    {
        var (context, body) = await RejectAsync(BuildOptions(), TimeSpan.FromSeconds(90));

        Assert.AreEqual("90", context.Response.Headers.RetryAfter.ToString());
        Assert.AreEqual(90, body.GetProperty("retryAfter").GetInt32());
        Assert.AreEqual("application/json", context.Response.ContentType);
        Assert.AreEqual("Rate limit exceeded", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// A token bucket does not report a retry-after, so the response has to fall back to a value
    /// rather than emitting an empty header or failing to parse one.
    /// </summary>
    [TestMethod]
    public async Task OnRejected_WithoutRetryAfterMetadata_FallsBackToSixtySeconds()
    {
        var (context, body) = await RejectAsync(BuildOptions(), retryAfter: null);

        Assert.AreEqual("60", context.Response.Headers.RetryAfter.ToString());
        Assert.AreEqual(60, body.GetProperty("retryAfter").GetInt32());
    }

    /// <summary>Sub-second retry-afters round to whole seconds, which must still parse as an int.</summary>
    [TestMethod]
    public async Task OnRejected_RoundsAFractionalRetryAfterToWholeSeconds()
    {
        var (context, body) = await RejectAsync(BuildOptions(), TimeSpan.FromMilliseconds(1500));

        Assert.AreEqual("2", context.Response.Headers.RetryAfter.ToString());
        Assert.AreEqual(2, body.GetProperty("retryAfter").GetInt32());
    }

    #endregion
}
