using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using RedMist.StatusApi.Controllers;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
// Several services declare a top-level Program, so name this one explicitly.
using StatusApiProgram = RedMist.StatusApi.Program;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers the limit on loading a whole session's laps: one call every ten seconds per caller.
/// </summary>
/// <remarks>
/// One API client polling LoadSessionLaps in bursts against an 8,000-lap endurance session was
/// enough to OOM-kill status pods during a live event. Each call reads, deserializes and
/// re-serializes every lap, and the global limiter allowed a burst of thirty of them.
/// </remarks>
[TestClass]
public sealed class StatusApiSessionLapsLimiterTests
{
    private static DefaultHttpContext Request(string ip, string? sub = "service-account-api-kamicoon", Action<DefaultHttpContext>? customize = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/v2/Events/LoadSessionLaps";
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (sub != null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], authenticationType: "Bearer"));
        }
        customize?.Invoke(context);
        return context;
    }

    private static string PartitionKeyFor(HttpContext context)
        => StatusApiProgram.GetSessionLapsPartition(context).PartitionKey;

    private static RateLimiter LimiterFor(HttpContext context)
        => StatusApiProgram.GetSessionLapsPartition(context).Factory("k");

    #region The allowance

    [TestMethod]
    public void TheFirstCall_IsAllowed()
    {
        using var limiter = LimiterFor(Request("203.0.113.7"));

        using var lease = limiter.AttemptAcquire();

        Assert.IsTrue(lease.IsAcquired);
    }

    [TestMethod]
    public void ASecondCallStraightAfter_IsRejected()
    {
        using var limiter = LimiterFor(Request("203.0.113.7"));
        using var first = limiter.AttemptAcquire();

        using var second = limiter.AttemptAcquire();

        Assert.IsFalse(second.IsAcquired, "One call per ten seconds means no burst beyond the first.");
    }

    [TestMethod]
    public void ABurst_GetsOnlyOneThrough()
    {
        using var limiter = LimiterFor(Request("203.0.113.7"));
        var leases = Enumerable.Range(0, 30).Select(_ => limiter.AttemptAcquire()).ToList();

        Assert.AreEqual(1, leases.Count(l => l.IsAcquired), "This burst is what the global limiter let through, and what OOM-killed the pod.");

        leases.ForEach(l => l.Dispose());
    }

    [TestMethod]
    public void ARejectedCall_DoesNotQueue()
    {
        using var limiter = LimiterFor(Request("203.0.113.7"));
        using var first = limiter.AttemptAcquire();

        var queued = limiter.AcquireAsync(1);

        Assert.IsTrue(queued.IsCompleted, "A held request still costs its full load when it runs.");
        using var lease = queued.Result;
        Assert.IsFalse(lease.IsAcquired);
    }

    /// <summary>The rejection says when to come back, which is up to the full ten seconds.</summary>
    [TestMethod]
    public void ARejectedCall_CarriesRetryAfterOfUpToTheInterval()
    {
        using var limiter = LimiterFor(Request("203.0.113.7"));
        using var first = limiter.AttemptAcquire();

        using var rejected = limiter.AttemptAcquire();

        Assert.IsTrue(rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        Assert.IsTrue(retryAfter > TimeSpan.Zero && retryAfter <= StatusApiProgram.SessionLapsInterval, $"Retry-After was {retryAfter}.");
    }

    [TestMethod]
    public void TheInterval_IsTenSeconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), StatusApiProgram.SessionLapsInterval);
    }

    #endregion

    #region Separation

    [TestMethod]
    public void TwoAddressesSharingASubject_GetSeparateBuckets()
    {
        // Every install of a viewer app shares one service-account subject.
        Assert.AreNotEqual(PartitionKeyFor(Request("203.0.113.7")), PartitionKeyFor(Request("198.51.100.22")));
    }

    [TestMethod]
    public void TwoSubjectsSharingAnAddress_GetSeparateBuckets()
    {
        Assert.AreNotEqual(PartitionKeyFor(Request("203.0.113.7", "integration-a")), PartitionKeyFor(Request("203.0.113.7", "integration-b")));
    }

    [TestMethod]
    public void AnAnonymousCaller_IsKeyedOnItsAddress()
    {
        var key = PartitionKeyFor(Request("203.0.113.7", sub: null));

        Assert.IsTrue(key.Contains("anonymous", StringComparison.Ordinal));
        Assert.IsTrue(key.Contains("203.0.113.7", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnrelatedHeaders_DoNotChangeTheBucket()
    {
        var plain = PartitionKeyFor(Request("203.0.113.7"));

        foreach (var header in new[] { "X-Client-Instance", "X-Device-Id", "User-Agent", "X-Request-Id" })
        {
            var withHeader = PartitionKeyFor(Request("203.0.113.7", customize: c => c.Request.Headers[header] = Guid.NewGuid().ToString()));
            Assert.AreEqual(plain, withHeader, $"{header} moved the caller to a different bucket, so rotating it escapes the limit.");
        }
    }

    [TestMethod]
    public void TheEventAndSession_DoNotChangeTheBucket()
    {
        var one = PartitionKeyFor(Request("203.0.113.7", customize: c => c.Request.QueryString = new QueryString("?eventId=521&sessionId=6")));
        var other = PartitionKeyFor(Request("203.0.113.7", customize: c => c.Request.QueryString = new QueryString("?eventId=522&sessionId=43")));

        Assert.AreEqual(one, other, "The limit is per caller, not per session, or cycling sessions escapes it.");
    }

    #endregion

    #region Wiring

    /// <summary>Read off the routed controllers, so an override or a <c>new</c> action there would fail this.</summary>
    [TestMethod]
    [DataRow(typeof(RedMist.StatusApi.Controllers.V1.EventsController))]
    [DataRow(typeof(RedMist.StatusApi.Controllers.V2.EventsController))]
    public void LoadSessionLaps_DeclaresThePolicy(Type controller)
    {
        var method = controller.GetMethod(nameof(EventsControllerBase.LoadSessionLaps))!;

        var attribute = method.GetCustomAttribute<EnableRateLimitingAttribute>(inherit: true);

        Assert.IsNotNull(attribute);
        Assert.AreEqual(StatusApiProgram.SessionLapsPolicy, attribute.PolicyName);
    }

    /// <summary>
    /// This tightens the global limiter rather than replacing it, so the global one still applies.
    /// </summary>
    [TestMethod]
    public void TheEndpoint_IsStillGloballyLimited()
    {
        var context = Request("203.0.113.7");
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(StatusApiProgram.SessionLapsPolicy)), "laps"));

        Assert.IsFalse(StatusApiProgram.IsExcludedFromGlobalLimiter(context));
    }

    #endregion
}
