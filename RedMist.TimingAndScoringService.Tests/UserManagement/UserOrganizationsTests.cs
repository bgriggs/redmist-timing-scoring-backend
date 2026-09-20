using Microsoft.AspNetCore.Mvc;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using RedMist.UserManagement.Models;

namespace RedMist.TimingAndScoringService.Tests.UserManagement;

/// <summary>
/// What an account gets back when it asks which organizations it belongs to, and how it then loads
/// one of them in full.
/// </summary>
/// <remarks>
/// These two endpoints replaced three. <c>LoadUserOrganization</c> answered with a single
/// organization taken from an unordered query, and <c>LoadUserOrganizationRoles</c> answered the
/// same question again with a role attached.
/// </remarks>
[TestClass]
public class UserOrganizationsTests
{
    private const string UserEmail = "driver@example.com";

    /// <summary>
    /// The reason this endpoint answers with a list at all. The endpoint it replaced took the first
    /// row of an unordered query over the mappings, so an account belonging to two organizations was
    /// handed an arbitrary one of them - in the test database, the API account rather than the
    /// racing organization - and the second was unreachable from the UI.
    /// </summary>
    [TestMethod]
    public async Task AUserBelongingToTwoOrganizations_GetsBoth()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 2, "Big Mission", "relay-test", "https://bigmission.example");
        SeedOrganization(db, 7, "Brian Griggs", "api-bgriggs");
        SeedMembership(db, UserEmail, 2, "admin");
        SeedMembership(db, UserEmail, 7, "Admin");
        await db.SaveChangesAsync();

        var organizations = await LoadAsync(controller);

        var bigMission = organizations.Single(o => o.OrganizationId == 2);
        Assert.AreEqual("Big Mission", bigMission.Name);
        Assert.AreEqual("relay-test", bigMission.ClientId);
        Assert.AreEqual("https://bigmission.example", bigMission.Website);
        Assert.AreEqual("admin", bigMission.Role);

        var griggs = organizations.Single(o => o.OrganizationId == 7);
        Assert.AreEqual("Brian Griggs", griggs.Name);
        Assert.AreEqual("api-bgriggs", griggs.ClientId);
        Assert.IsNull(griggs.Website, "An organization without a website reported one.");

        // Reported as stored rather than normalized, because callers compare it without regard to
        // case. Both spellings are in the production data.
        Assert.AreEqual("Admin", griggs.Role);
    }

    /// <summary>
    /// Two organizations can genuinely share a display name - ChampCar runs under two - so the name
    /// alone cannot order them. Without the id as a tiebreak the order would be back in the hands of
    /// the query plan, which is the defect this endpoint was reshaped to fix.
    /// </summary>
    [TestMethod]
    public async Task OrganizationsSharingAName_AreStillOrderedTheSameWayEveryTime()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 25, "ChampCar", "relay-champcar-ec");
        SeedOrganization(db, 4, "Wide Open Racing", "relay-wor");
        SeedOrganization(db, 5, "ChampCar", "relay-champcar");
        foreach (var id in new[] { 25, 4, 5 })
        {
            SeedMembership(db, UserEmail, id, "admin");
        }
        await db.SaveChangesAsync();

        var organizations = await LoadAsync(controller);

        CollectionAssert.AreEqual(new[] { 5, 25, 4 }, organizations.Select(o => o.OrganizationId).ToArray());
    }

    /// <summary>
    /// (Username, OrganizationId) is the primary key and Postgres compares it case sensitively, so
    /// an account can end up holding two mapping rows for one organization - self-registration
    /// writes the Keycloak spelling, and an administrator re-inviting the same person through the
    /// admin screen writes whatever they typed. This read is deliberately case-insensitive and
    /// matches both. The rows can disagree about the role, and a caller handed both has no basis to
    /// choose, so the same one has to come back every time.
    /// </summary>
    [TestMethod]
    public async Task TwoMappingRowsForOneOrganization_ProduceOneEntryWithAStableRole()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 2, "Big Mission", "relay-test");
        SeedMembership(db, "driver@example.com", 2, "admin");
        SeedMembership(db, "Driver@Example.com", 2, "Admin");
        await db.SaveChangesAsync();

        var memberships = await LoadAsync(controller);

        Assert.HasCount(1, memberships, "The organization was listed once per mapping row.");

        // The named value, not merely the same value twice: two calls in one process walk the rows
        // in the same order whatever the endpoint does, so comparing them to each other would pass
        // with no ordering at all. Postgres is under no such obligation - the username predicate is
        // not sargable, so the scan order can change under it.
        Assert.AreEqual("Admin", memberships.Single().Role);
    }

    /// <summary>
    /// Callers poll this on authentication state changes, and logos run to tens of kilobytes each -
    /// 60 KB for the largest in the test database. Carrying one per membership would put that on a
    /// hot path for a field nothing listing organizations reads.
    /// </summary>
    [TestMethod]
    public void TheListType_CannotCarryALogo()
    {
        // Asserted against the type rather than against a response, because the protection is that
        // the DTO has no such property: a test that read one off an instance could only ever
        // observe null and would pass just as happily after someone added the property back.
        Assert.IsNull(typeof(UserOrganizationDto).GetProperty("Logo"),
            "The list path grew a logo. It is polled on authentication state changes and logos run "
            + "to tens of kilobytes each; LoadOrganization carries the image instead.");
    }

    /// <summary>
    /// A mapping row whose organization no longer exists is the caller's least interesting problem.
    /// The inner join drops it, so one piece of stale bookkeeping cannot cost them the memberships
    /// that are real.
    /// </summary>
    [TestMethod]
    public async Task AMembershipInAnOrganizationThatNoLongerExists_IsDroppedNotFatal()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 2, "Big Mission", "relay-test");
        SeedMembership(db, UserEmail, 2, "admin");
        SeedMembership(db, UserEmail, 99, "admin");
        await db.SaveChangesAsync();

        var memberships = await LoadAsync(controller);

        CollectionAssert.AreEqual(new[] { 2 }, memberships.Select(m => m.OrganizationId).ToArray());
    }

    /// <summary>
    /// Belonging to an organization the database has lost is not the same as belonging to none, so
    /// it does not quietly answer with a blank record.
    /// </summary>
    [TestMethod]
    public async Task LoadingAnOrganizationThatNoLongerExists_IsNotFound()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedMembership(db, UserEmail, 99, "admin");
        await db.SaveChangesAsync();

        Assert.IsInstanceOfType<NotFoundObjectResult>((await controller.LoadOrganization(99)).Result);
    }

    /// <summary>
    /// The one 404 the list kept. An authenticated request with no username in its claims is a
    /// broken token, not an account without organizations, and must not read as "you have none".
    /// </summary>
    [TestMethod]
    public async Task ARequestWithNoUsernameInItsClaims_IsNotFound()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 2, "Big Mission", "relay-test");
        SeedMembership(db, UserEmail, 2, "admin");
        await db.SaveChangesAsync();

        controller.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity());

        Assert.IsInstanceOfType<NotFoundObjectResult>((await controller.LoadUserOrganizations()).Result);
    }

    /// <summary>
    /// An account that has not created an organization yet is in an ordinary state, not a failed
    /// one. The endpoint this replaced answered 404, which made the caller route on a caught
    /// exception.
    /// </summary>
    [TestMethod]
    public async Task AnAccountWithNoOrganizations_GetsAnEmptyListRatherThanAnError()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 2, "Big Mission", "relay-test");
        SeedMembership(db, "someone.else@example.com", 2, "admin");
        await db.SaveChangesAsync();

        var result = await controller.LoadUserOrganizations();

        Assert.IsNull(result.Result, "An empty membership list was reported as a failure.");
        Assert.IsEmpty(result.Value!);
    }

    /// <summary>
    /// PostgreSQL compares text case-sensitively, so a mapping row whose case differs from the
    /// Keycloak username would otherwise leave the account with no organizations at all.
    /// </summary>
    [TestMethod]
    public async Task AMappingRowDifferingOnlyInCase_IsStillAMembership()
    {
        var (controller, db) = RecordingOrganizationController.Create("Driver@Example.com");
        SeedOrganization(db, 2, "Big Mission", "relay-test");
        SeedMembership(db, "driver@example.com", 2, "admin");
        await db.SaveChangesAsync();

        Assert.HasCount(1, await LoadAsync(controller));
    }

    /// <summary>
    /// The logo the list deliberately leaves out has to be reachable somewhere, or the organization
    /// editor has nothing to show - and saving a record whose logo bytes could not be read would
    /// blank the logo.
    /// </summary>
    [TestMethod]
    public async Task LoadingOneOrganization_CarriesItsLogo()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 2, "Big Mission", "relay-test", logo: [1, 2, 3, 4]);
        SeedMembership(db, UserEmail, 2, "admin");
        await db.SaveChangesAsync();

        var org = (await controller.LoadOrganization(2)).Value!;

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, org.Logo);
        Assert.AreEqual("relay-test", org.ClientId);
    }

    [TestMethod]
    public async Task AnOrganizationWithNoLogo_FallsBackToTheSharedDefault()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 2, "Big Mission", "relay-test", logo: null);
        SeedMembership(db, UserEmail, 2, "admin");
        db.DefaultOrgImages.Add(new DefaultOrgImage { Id = 1, ImageData = [9, 9] });
        await db.SaveChangesAsync();

        CollectionAssert.AreEqual(new byte[] { 9, 9 }, (await controller.LoadOrganization(2)).Value!.Logo);
    }

    /// <summary>
    /// The id arrives from the caller rather than from the user's own mapping row, so membership has
    /// to be proven rather than assumed. Without this check any authenticated account could read any
    /// organization by guessing a small integer.
    /// </summary>
    [TestMethod]
    public async Task LoadingAnOrganizationTheUserDoesNotBelongTo_IsRefused()
    {
        var (controller, db) = RecordingOrganizationController.Create(UserEmail);
        SeedOrganization(db, 2, "Big Mission", "relay-test");
        SeedOrganization(db, 7, "Someone Else", "relay-other");
        SeedMembership(db, UserEmail, 2, "admin");
        await db.SaveChangesAsync();

        Assert.IsInstanceOfType<UnauthorizedObjectResult>((await controller.LoadOrganization(7)).Result);
    }

    private static async Task<List<UserOrganizationDto>> LoadAsync(RecordingOrganizationController controller)
        => (await controller.LoadUserOrganizations()).Value!;

    private static void SeedOrganization(Database.TsContext db, int id, string name, string clientId,
        string? website = null, byte[]? logo = null)
        => db.Organizations.Add(new Organization
        {
            Id = id,
            Name = name,
            ShortName = clientId,
            ClientId = clientId,
            Website = website,
            Logo = logo,
        });

    private static void SeedMembership(Database.TsContext db, string username, int organizationId, string role)
        => db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = username,
            OrganizationId = organizationId,
            Role = role,
        });
}
