using Keycloak.AuthServices.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedMist.Backend.Shared;
using RedMist.EventManagement;
using System.Security.Claims;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventManagement;

/// <summary>
/// The plumbing that carries a Keycloak realm role into [Authorize(Roles = ...)].
/// </summary>
/// <remarks>
/// Two independent settings have to name the same claim: the one the token handler stamps on the
/// identity as its role claim type, and the one the roles transformation writes its claims as.
/// IsInRole reads the first and finds nothing if the second disagrees, so a mismatch is not an error
/// -- it is a 403 for everybody, including a user who genuinely holds the role. Nothing else in this
/// solution gates on a role, so these are the tests that hold that agreement in place.
///
/// These run the service's own wiring (AddRedMistKeycloakAuth) against the service's own
/// appsettings, so an edit to either is an edit to what is tested here.
/// </remarks>
[TestClass]
public class SiteAdminRoleTests
{
    private const string SiteAdmin = Consts.SITE_ADMIN_ROLE;
    private const string EveryUser = "default-roles-redmist";

    /// <summary>
    /// The role name is only meaningful if it is the one Keycloak was configured with. Pinned here so
    /// that changing the constant has to be a deliberate act, matched in the realm and in the landing
    /// UI's route data, rather than an edit that quietly locks everyone out.
    /// </summary>
    [TestMethod]
    public void TheRoleName_IsPinned()
    {
        Assert.AreEqual("site-admin", Consts.SITE_ADMIN_ROLE);
    }

    /// <summary>
    /// EventManagement's own authentication and authorization wiring, over its own configuration.
    /// The appsettings file is linked into this project's output rather than transcribed, so the
    /// realm, authority and audience under test are the ones the service ships with.
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("EventManagement.appsettings.json", optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRedMistKeycloakAuth(configuration);

        return services.BuildServiceProvider();
    }

    private static JwtBearerOptions JwtOptions(ServiceProvider provider) =>
        provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

    /// <summary>
    /// Builds the principal the way the token handler does -- realm roles arrive as a single
    /// JSON-valued claim, not as individual role claims -- and runs it through the configured
    /// transformation. The role claim type is taken from the live options rather than assumed, so
    /// this cannot paper over the disagreement the class is about.
    /// </summary>
    private static async Task<ClaimsPrincipal> AuthenticateAsync(ServiceProvider provider, params string[] realmRoles)
    {
        var options = JwtOptions(provider);
        var realmAccess = JsonSerializer.Serialize(new { roles = realmRoles });
        var identity = new ClaimsIdentity(
            [new Claim(Keycloak.AuthServices.Common.KeycloakConstants.RealmAccessClaimType, realmAccess, "JSON")],
            authenticationType: "AuthenticationTypes.Federation",
            nameType: options.TokenValidationParameters.NameClaimType,
            roleType: options.TokenValidationParameters.RoleClaimType);

        var transformation = provider.GetRequiredService<IClaimsTransformation>();
        return await transformation.TransformAsync(new ClaimsPrincipal(identity));
    }

    private static Task<AuthorizationResult> AuthorizeAsync(ServiceProvider provider, ClaimsPrincipal user, string role)
    {
        // What [Authorize(Roles = role)] builds and evaluates.
        var policy = new AuthorizationPolicyBuilder().RequireRole(role).Build();
        return provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource: null, policy);
    }

    /// <summary>
    /// The agreement itself: the two live settings compared to each other, with no constant standing
    /// in for either. Asserting each against a constant separately would leave this passing under the
    /// very mismatch it is named for.
    /// </summary>
    [TestMethod]
    public void TheTokenHandlersRoleClaim_IsTheOneTheTransformationWrites()
    {
        using var provider = BuildServices();

        var writtenByTransformation = provider.GetRequiredService<IOptions<KeycloakAuthorizationOptions>>().Value.RoleClaimType;
        var readByIsInRole = JwtOptions(provider).TokenValidationParameters.RoleClaimType;

        Assert.AreEqual(writtenByTransformation, readByIsInRole,
            "IsInRole reads the identity's role claim type. If the token handler stamps a different one than " +
            "the roles transformation writes, every role check refuses every user.");
    }

    [TestMethod]
    public async Task AUserHoldingTheRealmRole_IsAllowed()
    {
        using var provider = BuildServices();
        var user = await AuthenticateAsync(provider, EveryUser, SiteAdmin);

        Assert.IsTrue(user.IsInRole(SiteAdmin), "The realm role did not survive into IsInRole");
        Assert.IsTrue((await AuthorizeAsync(provider, user, SiteAdmin)).Succeeded);
    }

    /// <summary>
    /// The role has to mean something. Every authenticated user in the realm holds
    /// default-roles-redmist, and the site-admin endpoints have to tell the two apart.
    /// </summary>
    [TestMethod]
    public async Task AnOrdinaryAuthenticatedUser_IsRefused()
    {
        using var provider = BuildServices();
        var user = await AuthenticateAsync(provider, EveryUser);

        Assert.IsTrue(user.IsInRole(EveryUser), "Precondition: the realm role mapping is working at all");
        Assert.IsFalse(user.IsInRole(SiteAdmin));
        Assert.IsFalse((await AuthorizeAsync(provider, user, SiteAdmin)).Succeeded);
    }

    /// <summary>
    /// Role names are compared exactly: a near miss must not open the door. Each case first confirms
    /// the near-miss role really was mapped, so these cannot pass against a transformation that has
    /// stopped mapping anything at all.
    /// </summary>
    [TestMethod]
    [DataRow("Site-Admin")]
    [DataRow("siteadmin")]
    [DataRow("site_admin")]
    [DataRow("admin")]
    public async Task ARoleThatMerelyResemblesIt_IsRefused(string role)
    {
        using var provider = BuildServices();
        var user = await AuthenticateAsync(provider, EveryUser, role);

        Assert.IsTrue(user.IsInRole(role), "Precondition: the near-miss role was mapped");
        Assert.IsFalse((await AuthorizeAsync(provider, user, SiteAdmin)).Succeeded);
    }

    /// <summary>
    /// A caller carrying no claims is refused. Note this is the requirement refusing an empty
    /// principal, not authentication being enforced: the role requirement alone does not demand an
    /// authenticated identity, and it is AuthorizationMiddleware that turns the refusal into a
    /// challenge.
    /// </summary>
    [TestMethod]
    public async Task ACallerWithNoClaims_IsRefused()
    {
        using var provider = BuildServices();

        Assert.IsFalse((await AuthorizeAsync(provider, new ClaimsPrincipal(new ClaimsIdentity()), SiteAdmin)).Succeeded);
    }
}
