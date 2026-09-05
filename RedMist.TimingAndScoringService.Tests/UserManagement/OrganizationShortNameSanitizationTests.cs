using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RedMist.Database;

namespace RedMist.TimingAndScoringService.Tests.UserManagement;

/// <summary>
/// The short name a registrant types decides three things that have to agree: the Keycloak client
/// ID, the stored <c>Organization.ShortName</c> that Kubernetes job names are built from, and the
/// name the availability endpoints check. The availability endpoints resolve the name before
/// checking, so these tests pin that creation resolves it identically - otherwise "ABC" reports
/// available against relay-abc and then creates relay-ABC.
/// </summary>
[TestClass]
public class OrganizationShortNameSanitizationTests
{
    private const string UserEmail = "driver@example.com";

    private TsContext db = null!;
    private RecordingOrganizationController controller = null!;

    [TestInitialize]
    public void Setup() => (controller, db) = RecordingOrganizationController.Create(UserEmail);

    [TestCleanup]
    public void Cleanup() => db?.Dispose();

    /// <summary>
    /// The invariant the whole change is about, asserted end to end rather than against a
    /// hand-written expectation: whatever the availability endpoint looked up is exactly what
    /// creation goes on to provision and store.
    /// </summary>
    [TestMethod]
    [DataRow("ABC")]
    [DataRow("Acme")]
    [DataRow("A B")]
    [DataRow("Ac me!")]
    [DataRow("-ACME-")]
    [DataRow("A--B")]
    // Longer raw than the resolved name: only usable because the bound is applied after sanitizing.
    [DataRow("SCCA - SF")]
    public async Task TheNameCheckedForAvailability_IsTheNameCreated(string requested)
    {
        await controller.RelayClientNameExists(requested);
        var checkedClientId = controller.ClientsLookedUpFor.Single();

        await controller.SaveNewOrganization(RecordingOrganizationController.NewOrg(requested));

        var org = db.Organizations.Single();
        Assert.AreEqual(checkedClientId, org.ClientId);
        CollectionAssert.AreEqual(new[] { checkedClientId }, controller.ClientsCreated);
    }

    [TestMethod]
    [DataRow("ABC")]
    [DataRow("Ac me!")]
    public async Task TheApiNameCheckedForAvailability_IsTheApiNameCreated(string requested)
    {
        await controller.ApiClientNameExistsAsync(requested);
        var checkedClientId = controller.ClientsLookedUpFor.Single();

        await controller.SaveNewApiUserAsync(RecordingOrganizationController.NewOrg(requested));

        Assert.AreEqual(checkedClientId, db.Organizations.Single().ClientId);
    }

    /// <summary>
    /// The exact case the availability check got wrong: an upper-case short name reports available
    /// against relay-abc, so creation has to produce relay-abc and not relay-ABC.
    /// </summary>
    [TestMethod]
    public async Task SaveNewOrganization_LowerCasesTheShortNameTheAvailabilityCheckWouldHaveUsed()
    {
        var result = await controller.SaveNewOrganization(RecordingOrganizationController.NewOrg("ABC"));

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var org = db.Organizations.Single();
        Assert.AreEqual("relay-abc", org.ClientId);
        Assert.AreEqual("abc", org.ShortName);
        CollectionAssert.AreEqual(new[] { "relay-abc" }, controller.ClientsCreated);
    }

    /// <summary>
    /// ShortName is what <c>OrchestrationService</c> builds Kubernetes job names from, so it has to
    /// come out as a valid DNS-1123 label - lower case, no spaces or punctuation, no dash runs and
    /// no leading or trailing dash.
    /// </summary>
    [TestMethod]
    [DataRow("A B", "a-b")]
    [DataRow("Ac me!", "ac-me")]
    [DataRow("-ACME-", "acme")]
    [DataRow("A--B", "a-b")]
    public async Task SaveNewOrganization_StoresTheSanitizedShortName(string requested, string expected)
    {
        await controller.SaveNewOrganization(RecordingOrganizationController.NewOrg(requested));

        var org = db.Organizations.Single();
        Assert.AreEqual(expected, org.ShortName);
        Assert.AreEqual($"relay-{expected}", org.ClientId);
    }

    [TestMethod]
    public async Task SaveNewApiUser_SanitizesTheShortNameTheSameWay()
    {
        var result = await controller.SaveNewApiUserAsync(RecordingOrganizationController.NewOrg("ABC"));

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var org = db.Organizations.Single();
        Assert.AreEqual("api-abc", org.ClientId);
        Assert.AreEqual("abc", org.ShortName);
    }

    /// <summary>
    /// Length is checked on the resolved name, not the raw one: "ab!" is three characters but leaves
    /// only two behind, and "abcdefghi" is nine. The bare-prefix cases matter more - without a guard
    /// the client ID would be "relay-" and every such registration would collide with the last one.
    /// </summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("!!!")]
    [DataRow("---")]
    [DataRow("ab!")]
    [DataRow("abcdefghi")]
    public async Task SaveNewOrganization_WithAnUnusableShortName_IsRejected(string requested)
    {
        var result = await controller.SaveNewOrganization(RecordingOrganizationController.NewOrg(requested));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, db.Organizations.Count());
        Assert.AreEqual(0, controller.ClientsCreated.Count);
    }

    [TestMethod]
    [DataRow("!!!")]
    [DataRow("ab!")]
    public async Task SaveNewApiUser_WithAnUnusableShortName_IsRejected(string requested)
    {
        var result = await controller.SaveNewApiUserAsync(RecordingOrganizationController.NewOrg(requested));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, db.Organizations.Count());
        Assert.AreEqual(0, controller.ClientsCreated.Count);
    }

    /// <summary>
    /// A name creation would refuse must not be reported as free, or the caller gets a green light
    /// and then a 400 on submit.
    /// </summary>
    [TestMethod]
    [DataRow("!!!")]
    [DataRow("ab!")]
    [DataRow("abcdefghi")]
    public async Task NameChecks_WithAnUnusableShortName_AreRejectedRatherThanReportedFree(string requested)
    {
        var relay = await controller.RelayClientNameExists(requested);
        var api = await controller.ApiClientNameExistsAsync(requested);

        Assert.IsInstanceOfType<BadRequestObjectResult>(relay.Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>(api.Result);
        Assert.AreEqual(0, controller.ClientsLookedUpFor.Count);
    }

    /// <summary>
    /// The generated clients read title and errors off a 400, so a bare string body would reach the
    /// caller as an undefined message and the explanation would be lost.
    /// </summary>
    [TestMethod]
    public async Task AnUnusableShortName_IsRejectedAsValidationProblemDetails()
    {
        var result = await controller.SaveNewOrganization(RecordingOrganizationController.NewOrg("!!!"));

        var body = (ValidationProblemDetails)((BadRequestObjectResult)result.Result!).Value!;
        Assert.AreEqual(StatusCodes.Status400BadRequest, body.Status);
        var message = body.Errors["ShortName"].Single();
        StringAssert.Contains(message, "3 to 8");
    }

    [TestMethod]
    public async Task RelayClientNameExists_ReportsATakenNameRegardlessOfHowItWasTyped()
    {
        controller.ExistingClients.Add("relay-acme");

        var taken = await controller.RelayClientNameExists("ACME");
        var free = await controller.RelayClientNameExists("acme2");

        Assert.IsTrue(taken.Value);
        Assert.IsFalse(free.Value);
    }

    /// <summary>
    /// Resolving an already-resolved name has to be a no-op, or a name that passed the availability
    /// check could still be rewritten on its way into Keycloak.
    /// </summary>
    [TestMethod]
    [DataRow("ABC")]
    [DataRow("A B")]
    [DataRow("Ac me!")]
    [DataRow("-ACME-")]
    public async Task ResolvingAnAlreadyResolvedShortName_ChangesNothing(string requested)
    {
        await controller.RelayClientNameExists(requested);
        var once = controller.ClientsLookedUpFor.Single();

        await controller.RelayClientNameExists(once["relay-".Length..]);

        // Asserting the whole list, not just the last entry: a second call that was rejected outright
        // would record nothing and still leave the last entry equal to the first.
        CollectionAssert.AreEqual(new[] { once, once }, controller.ClientsLookedUpFor);
    }

    /// <summary>
    /// The registration email has to name the client that was actually provisioned, not the raw text
    /// the registrant typed, or the credentials in it will not match anything in Keycloak.
    /// </summary>
    [TestMethod]
    public async Task SaveNewOrganization_EmailsTheProvisionedClientIdNotTheRawShortName()
    {
        await controller.SaveNewOrganization(RecordingOrganizationController.NewOrg("ABC"));
        await controller.WaitForEmailAsync();

        var sent = controller.Sent.Single();
        Assert.AreEqual("Red Mist Organization Registration", sent.Subject);
        Assert.AreEqual(UserEmail, sent.To);
        StringAssert.Contains(sent.Body, "relay-abc");
        Assert.IsFalse(sent.Body.Contains("relay-ABC"), "The email must name the provisioned client, not the raw short name.");
    }

    /// <summary>
    /// The user's organization mapping keys off the account email, so it has to survive a change that
    /// rewrites the short name.
    /// </summary>
    [TestMethod]
    public async Task SaveNewOrganization_StillMapsTheRegistrantToTheNewOrganization()
    {
        await controller.SaveNewOrganization(RecordingOrganizationController.NewOrg("ABC"));

        var org = db.Organizations.Single();
        var mapping = db.UserOrganizationMappings.Single();
        Assert.AreEqual(UserEmail, mapping.Username);
        Assert.AreEqual(org.Id, mapping.OrganizationId);
    }
}
