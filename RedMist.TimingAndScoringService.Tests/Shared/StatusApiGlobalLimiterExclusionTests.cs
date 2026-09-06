using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
// Several services declare a top-level Program, so name this one explicitly.
using StatusApiProgram = RedMist.StatusApi.Program;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers which requests the global limiter lets past to their own limiter.
/// </summary>
/// <remarks>
/// Live session polling has to be excluded: the global limiter queues, and a queued session state
/// arrives out of step with the delta subscription feeding the same screen. That exclusion used to
/// be a list of literal paths, which is a list that has to be kept in step with every route the
/// actions answer on - and these are versioned through a <c>v{version:apiVersion}</c> constraint,
/// so they answer on more than the one spelling anybody writes down. A path the list falls behind
/// on is not an error; it just quietly stops being excluded.
///
/// So the question these ask is whether the exclusion follows the endpoint rather than its address.
/// </remarks>
[TestClass]
public sealed class StatusApiGlobalLimiterExclusionTests
{
    /// <summary>A routed request, as the limiter sees one once routing has run.</summary>
    private static DefaultHttpContext RequestTo(string path, string? policy)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        var metadata = policy is null
            ? new EndpointMetadataCollection()
            : new EndpointMetadataCollection(new EnableRateLimitingAttribute(policy));
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, path));

        return context;
    }

    /// <summary>
    /// Every spelling the polling actions answer on.
    /// </summary>
    /// <remarks>
    /// The path is what these rows vary, and on this branch the path is no longer read at all -
    /// which is the point, and is why they fail against the old list, which did read it. What they
    /// cannot show is the premise: that a request to "/v2.0/Events/GetCurrentSessionState" reaches
    /// the endpoint in the first place. That was established outside this suite, by driving these
    /// paths through an app configured the same way - "/v2.0/", "/v02/", "/v2.00/" and "/v1.0/" all
    /// resolved to the controller and none of them appeared in the old list. Pinning it here would
    /// take a test that boots the real pipeline, which needs a database and Redis.
    /// </remarks>
    [TestMethod]
    [DataRow("/Events/GetCurrentSessionState", DisplayName = "Legacy unversioned route")]
    [DataRow("/v1/Events/GetCurrentSessionState", DisplayName = "v1")]
    [DataRow("/v1.0/Events/GetCurrentSessionState", DisplayName = "v1.0")]
    [DataRow("/v2/Events/GetCurrentSessionState", DisplayName = "v2")]
    [DataRow("/v2.0/Events/GetCurrentSessionState", DisplayName = "v2.0")]
    [DataRow("/v2/Events/GetCurrentSessionStateJson", DisplayName = "The JSON variant")]
    [DataRow("/v2.0/Events/GetCurrentLegacySessionPayload", DisplayName = "The legacy payload variant")]
    [DataRow("/v3/Events/GetCurrentSessionState", DisplayName = "A version that does not exist yet")]
    public void APollingEndpoint_IsExcludedWhateverItsPath(string path)
    {
        Assert.IsTrue(StatusApiProgram.IsExcludedFromGlobalLimiter(RequestTo(path, StatusApiProgram.SessionPollingPolicy)),
            "Polling has its own limiter; letting the global one queue it is what the exclusion exists to prevent.");
    }

    [TestMethod]
    [DataRow("/SponsorTelemetry/Impression")]
    [DataRow("/v1/SponsorTelemetry/Impression")]
    [DataRow("/v1.0/SponsorTelemetry/Impression")]
    public void ASponsorTelemetryEndpoint_IsExcludedWhateverItsPath(string path)
    {
        Assert.IsTrue(StatusApiProgram.IsExcludedFromGlobalLimiter(RequestTo(path, StatusApiProgram.SponsorTelemetryPolicy)));
    }

    /// <summary>
    /// The hub is mapped at a literal path with no policy of its own, so it is the one thing still
    /// matched by address - and nothing versions it.
    /// </summary>
    [TestMethod]
    public void TheStatusHub_IsStillExcluded()
    {
        Assert.IsTrue(StatusApiProgram.IsExcludedFromGlobalLimiter(RequestTo("/event-status", policy: null)));
        Assert.IsTrue(StatusApiProgram.IsExcludedFromGlobalLimiter(RequestTo("/event-status/negotiate", policy: null)));
    }

    /// <summary>
    /// Matching the hub by address is only safe while it also has to have routed. Otherwise the one
    /// branch still keyed on a path is a way to opt out of the limiter by choosing a URL.
    /// </summary>
    [TestMethod]
    public void AHubPathThatRoutedNowhere_IsStillGloballyLimited()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/event-status/nothing-is-mapped-here";

        Assert.IsFalse(StatusApiProgram.IsExcludedFromGlobalLimiter(context));
    }

    /// <summary>
    /// Exports carry their own policy too, but theirs tightens the global limiter rather than
    /// replacing it. Excluding everything that names a policy would have quietly lifted their cap.
    /// </summary>
    [TestMethod]
    [DataRow(StatusApiProgram.ExportsPolicy)]
    [DataRow(StatusApiProgram.ExportsAvailabilityPolicy)]
    public void AnExportEndpoint_IsStillGloballyLimited(string policy)
    {
        Assert.IsFalse(StatusApiProgram.IsExcludedFromGlobalLimiter(RequestTo("/v1/Exports/Session", policy)));
    }

    /// <summary>
    /// The nearest attribute wins, which is what lets an export action override its controller. The
    /// exclusion reads whichever that is, so the ordering matters to it.
    /// </summary>
    [TestMethod]
    public void WhereAnActionOverridesItsController_TheActionsPolicyIsRead()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/Exports/GetAvailability";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(
                new EnableRateLimitingAttribute(StatusApiProgram.ExportsPolicy),
                new EnableRateLimitingAttribute(StatusApiProgram.ExportsAvailabilityPolicy)),
            "availability"));

        Assert.IsFalse(StatusApiProgram.IsExcludedFromGlobalLimiter(context),
            "Neither policy replaces the global limiter, so this is excluded either way - but it reads the last.");
    }

    [TestMethod]
    public void AnOrdinaryEndpoint_IsStillGloballyLimited()
    {
        Assert.IsFalse(StatusApiProgram.IsExcludedFromGlobalLimiter(RequestTo("/v2/Events/LoadEvent", policy: null)));
    }

    /// <summary>
    /// A path that matched no endpoint has no policy to read. It is on its way to a 404, so the
    /// global limiter is the right place for it - and a caller must not be able to dodge the limiter
    /// by dressing a request up as a polling URL.
    /// </summary>
    [TestMethod]
    public void AnUnroutedRequest_IsStillGloballyLimited()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/v2/Events/GetCurrentSessionState";

        Assert.IsFalse(StatusApiProgram.IsExcludedFromGlobalLimiter(context),
            "Nothing routed here, so the path is just a string the caller chose.");
    }
}
