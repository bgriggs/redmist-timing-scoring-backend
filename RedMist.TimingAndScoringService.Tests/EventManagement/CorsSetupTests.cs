using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedMist.EventManagement;

namespace RedMist.TimingAndScoringService.Tests.EventManagement;

/// <summary>
/// The cross-origin policy, which the admin page in the landing UI depends on.
/// </summary>
/// <remarks>
/// This service was reached only by desktop and server callers until that page, and neither enforces
/// CORS, so nothing here was ever exercised by a browser. The failure it went out with was partial
/// rather than total, which is worse: a policy naming no methods still satisfies a preflight for the
/// safelisted three, so every read and every POST worked and only the PUT endpoints were refused.
///
/// Asserted by running an actual preflight through the real CorsService rather than by reading the
/// policy's flags. The flags are one way to arrive at a correct answer, not the answer itself -- a
/// policy naming the landing UI's origins would be stricter and better and would fail a flag check.
/// </remarks>
[TestClass]
public class CorsSetupTests
{
    /// <summary>
    /// The response headers a browser would get back from a preflight, from the service's own policy.
    /// </summary>
    private static IHeaderDictionary Preflight(string method, string requestHeaders = "authorization")
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddRedMistCors()
            .BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Options;
        context.Request.Headers.Origin = "https://redmist.racing";
        context.Request.Headers.AccessControlRequestMethod = method;
        context.Request.Headers.AccessControlRequestHeaders = requestHeaders;

        var policy = provider.GetRequiredService<ICorsPolicyProvider>()
            .GetPolicyAsync(context, policyName: null).GetAwaiter().GetResult();
        Assert.IsNotNull(policy, "There is no default CORS policy at all");

        var service = provider.GetRequiredService<ICorsService>();
        service.ApplyResult(service.EvaluatePolicy(context, policy), context.Response);
        return context.Response.Headers;
    }

    /// <summary>
    /// PUT is the case that was actually broken, and the one a flags check would not have caught in
    /// the form it was written.
    /// </summary>
    [TestMethod]
    [DataRow("GET")]
    [DataRow("POST")]
    [DataRow("PUT")]
    [DataRow("DELETE")]
    public void APreflight_IsAnsweredWithTheMethodItAsksAbout(string method)
    {
        var headers = Preflight(method);

        Assert.AreEqual(method, headers.AccessControlAllowMethods.ToString(),
            "A preflight the browser can act on has to name the method back. Without this, anything "
            + "outside the safelisted GET, HEAD and POST is refused before it is sent.");
    }

    /// <summary>
    /// Authorization is not a safelisted request header, so a preflight that does not allow it back
    /// refuses every authenticated call -- which here is all of them.
    /// </summary>
    [TestMethod]
    public void APreflight_AllowsTheHeadersABearerTokenNeeds()
    {
        var headers = Preflight("POST", "authorization,content-type");

        Assert.AreEqual("authorization,content-type", headers.AccessControlAllowHeaders.ToString());
    }

    [TestMethod]
    public void APreflight_AllowsTheOriginTheLandingUiIsServedFrom()
    {
        Assert.IsFalse(string.IsNullOrEmpty(Preflight("GET").AccessControlAllowOrigin.ToString()),
            "The landing UI is served from a different origin than this API");
    }

    /// <summary>
    /// Credentials are deliberately not asked for: the admin page authenticates with a bearer token
    /// in a header, and nothing here relies on ambient authority.
    /// </summary>
    /// <remarks>
    /// This guards a total outage rather than a browser nicety. Combining credentials with the
    /// wildcard origin throws inside CorsService, so every request carrying an Origin header would
    /// answer 500 -- and naming origins instead would introduce a CSRF surface that does not exist
    /// while the token travels in a header.
    /// </remarks>
    [TestMethod]
    public void ThePolicy_DoesNotAskForCredentials()
    {
        Assert.IsTrue(string.IsNullOrEmpty(Preflight("POST").AccessControlAllowCredentials.ToString()));
    }
}
