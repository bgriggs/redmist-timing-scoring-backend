using Microsoft.AspNetCore.Http;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
// Several services declare a top-level Program, so name this one explicitly.
using StatusApiProgram = RedMist.StatusApi.Program;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers how StatusApi partitions live session polling between callers.
/// </summary>
/// <remarks>
/// This is where a live event went wrong. The viewer apps authenticate with client credentials, so
/// every install presents a token carrying the same service-account <c>sub</c>. The polling limiter
/// keyed on that subject, which put the entire user base in one bucket of roughly a request a
/// second, and viewers polling every five seconds drew several hundred 429s in an afternoon.
///
/// Two properties matter and they pull against each other. Callers have to be separated, or the
/// limit is one allowance shared by everyone. And the thing separating them has to be something a
/// caller cannot choose, or the limit is advisory - this endpoint is anonymous, uncached, and
/// excluded from the global limiter, so its own limiter is all there is in front of it.
/// </remarks>
[TestClass]
public sealed class StatusApiCallerKeyTests
{
    private const string ServiceAccountSub = "service-account-redmist-ios-ui";

    /// <summary>A request from a viewer app, authenticated as the shared service account.</summary>
    private static DefaultHttpContext ViewerRequest(string? ip, Action<DefaultHttpContext>? customize = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/v2/Events/GetCurrentSessionState";
        if (ip != null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        }
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", ServiceAccountSub), new Claim("client_id", "redmist-ios-ui")],
            authenticationType: "Bearer"));
        customize?.Invoke(context);
        return context;
    }

    private static string PartitionKeyFor(HttpContext context)
        => StatusApiProgram.GetSessionPollingPartition(context).PartitionKey;

    /// <summary>Attempts <paramref name="attempts"/> acquisitions, returning how many were allowed.</summary>
    private static int AcquireCount(RateLimiter limiter, int attempts)
    {
        var allowed = 0;
        var leases = new List<RateLimitLease>();
        for (var i = 0; i < attempts; i++)
        {
            var lease = limiter.AttemptAcquire();
            leases.Add(lease);
            if (lease.IsAcquired)
            {
                allowed++;
            }
        }
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
        return allowed;
    }

    #region Separation

    [TestMethod]
    public void TwoAddresses_GetSeparateBuckets()
    {
        Assert.AreNotEqual(PartitionKeyFor(ViewerRequest("203.0.113.7")), PartitionKeyFor(ViewerRequest("198.51.100.22")));
    }

    [TestMethod]
    public void ThePartitionKey_DoesNotUseTheSubjectClaim()
    {
        var key = PartitionKeyFor(ViewerRequest("203.0.113.7"));

        Assert.IsFalse(key.Contains(ServiceAccountSub, StringComparison.Ordinal),
            "Every install shares this subject, so a key built from it is one bucket for the whole user base.");
    }

    [TestMethod]
    public void ForwardedHeaders_AreUsedAheadOfTheSocketAddress()
    {
        // Behind Cloudflare the socket address is the proxy's, identical for everyone.
        var context = ViewerRequest("10.0.0.1", c => c.Request.Headers["CF-Connecting-IP"] = "203.0.113.7");

        Assert.IsTrue(PartitionKeyFor(context).Contains("203.0.113.7", StringComparison.Ordinal),
            "Keying on the proxy address would put every viewer in one bucket again.");
    }

    #endregion

    #region Not forgeable

    /// <summary>
    /// The reason the key is the address and nothing else. Anything a caller sets can be rotated per
    /// request, and a fresh partition per request is an unlimited allowance plus an unbounded
    /// dictionary - on an endpoint whose only protection this is.
    /// </summary>
    [TestMethod]
    public void HeadersTheCallerChooses_DoNotChangeTheBucket()
    {
        var plain = PartitionKeyFor(ViewerRequest("203.0.113.7"));

        foreach (var header in new[] { "X-Client-Instance", "X-Device-Id", "User-Agent", "X-Request-Id" })
        {
            var withHeader = PartitionKeyFor(ViewerRequest("203.0.113.7", c => c.Request.Headers[header] = Guid.NewGuid().ToString()));

            Assert.AreEqual(plain, withHeader, $"{header} moved the caller to a different bucket, so rotating it escapes the limit.");
        }
    }

    /// <summary>
    /// X-Forwarded-For is caller-settable in general; it is trusted here only because Cloudflare
    /// overwrites CF-Connecting-IP at the edge, which is checked first. Worth pinning: if the
    /// preference order were ever flipped, the limit would become opt-out from the internet.
    /// </summary>
    [TestMethod]
    public void CloudflareAddress_WinsOverASpoofedForwardedFor()
    {
        var context = ViewerRequest("10.0.0.1", c =>
        {
            c.Request.Headers["CF-Connecting-IP"] = "203.0.113.7";
            c.Request.Headers["X-Forwarded-For"] = "198.51.100.99";
        });

        Assert.IsTrue(PartitionKeyFor(context).Contains("203.0.113.7", StringComparison.Ordinal));
    }

    #endregion

    #region The allowance

    /// <summary>
    /// A viewer polls every five seconds and retries a failure up to three times, and several
    /// viewers can share an address. The old fixed window of one per 0.9s admitted the poll and
    /// rejected its own retries.
    /// </summary>
    [TestMethod]
    public void TheBucket_AbsorbsAGroupOfViewersStartingAtOnce()
    {
        using var limiter = StatusApiProgram.GetSessionPollingPartition(ViewerRequest("203.0.113.7")).Factory("k");

        Assert.AreEqual(15, AcquireCount(limiter, 15), "A burst of fifteen has to get through - that is several viewers arriving together.");
    }

    [TestMethod]
    public void TheBucket_StillStopsACallerThatRunsAway()
    {
        using var limiter = StatusApiProgram.GetSessionPollingPartition(ViewerRequest("203.0.113.7")).Factory("k");

        Assert.AreEqual(15, AcquireCount(limiter, 500), "Past the burst the caller has to be held to the refill rate.");
    }

    [TestMethod]
    public void TheBucket_DoesNotQueue()
    {
        using var limiter = StatusApiProgram.GetSessionPollingPartition(ViewerRequest("203.0.113.7")).Factory("k");
        AcquireCount(limiter, 15);

        var queued = limiter.AcquireAsync(1);

        Assert.IsTrue(queued.IsCompleted, "A queued session state is stale by the time it is served.");
        using var lease = queued.Result;
        Assert.IsFalse(lease.IsAcquired);
    }

    /// <summary>
    /// The client backs off 500ms and retries, so a rejection has to say when it is worth returning
    /// or the retry is spent on a bucket that is still empty.
    /// </summary>
    [TestMethod]
    public void ARejectedRequest_CarriesRetryAfter()
    {
        using var limiter = StatusApiProgram.GetSessionPollingPartition(ViewerRequest("203.0.113.7")).Factory("k");
        AcquireCount(limiter, 15);

        using var rejected = limiter.AttemptAcquire();

        Assert.IsFalse(rejected.IsAcquired, "Sanity check - the bucket is spent.");
        Assert.IsTrue(rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        Assert.IsTrue(retryAfter > TimeSpan.Zero);
    }

    #endregion
}
