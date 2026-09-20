using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Controllers;
using RedMist.EventProcessor.Tests.Utilities;
using StackExchange.Redis;
using System.Security.Claims;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;

namespace RedMist.TimingAndScoringService.Tests.EventManagement.Controllers;

/// <summary>
/// Event management reached by a person rather than by a relay.
/// </summary>
/// <remarks>
/// <para>
/// Every action here used to resolve the caller by matching the token's <c>client_id</c> against
/// <c>Organization.ClientId</c>. That claim identifies a Keycloak client, and an organization's
/// client is its relay or API machine account - so a signed-in human, whose token is issued to the
/// web application, matched no organization at all. Event lists came back empty and every write
/// answered 404, which is why no person could administer an event from the web.
/// </para>
/// <para>
/// Membership now comes from <c>UserOrganizationMappings</c> for a person and from the client id for
/// a machine, through one resolver. These tests cover the half that never worked.
/// </para>
/// </remarks>
[TestClass]
public class SignedInOrganizerAccessTests
{
    private const string Organizer = "organizer@example.com";
    private const int MineId = 1;
    private const int TheirsId = 2;
    private const int BothId = 3;

    private IDbContextFactory<TsContext> dbFactory = null!;
    private TsContext db = null!;
    private OrganizerEventController events = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var database = new Mock<IDatabase>();
        database.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("1-1"));
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db = dbFactory.CreateDbContext();
        events = new OrganizerEventController(loggerFactory.Object, dbFactory, mux.Object);
    }

    [TestCleanup]
    public void Cleanup() => db.Dispose();

    /// <summary>
    /// A person's token carries a username and a client id belonging to the web application, which
    /// matches no organization. Signing in as one is what the old resolution could not do.
    /// </summary>
    private void SignIn(string? username, string clientId = "redmist-landing")
    {
        List<Claim> claims = [new Claim("client_id", clientId)];
        if (username != null)
        {
            claims.Add(new Claim(ClaimTypes.Name, username));
            claims.Add(new Claim("preferred_username", username));
        }
        events.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType")) }
        };
    }

    private async Task SeedAsync()
    {
        db.Organizations.AddRange(
            new Organization { Id = MineId, ClientId = "relay-mine", Name = "Mine", ShortName = "M" },
            new Organization { Id = TheirsId, ClientId = "relay-theirs", Name = "Theirs", ShortName = "T" },
            new Organization { Id = BothId, ClientId = "api-both", Name = "Both", ShortName = "B" });
        db.UserOrganizationMappings.AddRange(
            new UserOrganizationMapping { Username = Organizer, OrganizationId = MineId, Role = "admin" },
            new UserOrganizationMapping { Username = Organizer, OrganizationId = BothId, Role = "admin" });
        db.Events.AddRange(
            NewEvent(10, MineId, "Mine event"),
            NewEvent(20, TheirsId, "Their event"),
            NewEvent(30, BothId, "Both event"));
        await db.SaveChangesAsync();
    }

    private static ConfigEvent NewEvent(int id, int orgId, string name) => new()
    {
        Id = id,
        OrganizationId = orgId,
        Name = name,
        StartDate = new DateTime(2026, 1, 1),
        EndDate = new DateTime(2026, 1, 2),
    };

    [TestMethod]
    public async Task AnOrganizerSeesTheEventsOfAnOrganizationTheyAdminister()
    {
        await SeedAsync();
        SignIn(Organizer);

        var summaries = await events.LoadEventSummaries(MineId);

        CollectionAssert.AreEqual(new[] { 10 }, summaries.Select(e => e.Id).ToArray());
    }

    /// <summary>
    /// Each organization is asked for by id, so holding two does not leak one into the other.
    /// </summary>
    [TestMethod]
    public async Task AnOrganizerWithTwoOrganizations_SeesEachSeparately()
    {
        await SeedAsync();
        SignIn(Organizer);

        Assert.AreEqual(10, (await events.LoadEventSummaries(MineId)).Single().Id);
        Assert.AreEqual(30, (await events.LoadEventSummaries(BothId)).Single().Id);
    }

    [TestMethod]
    public async Task AnOrganizerAskingForAnOrganizationTheyDoNotAdminister_SeesNothing()
    {
        await SeedAsync();
        SignIn(Organizer);

        Assert.IsEmpty(await events.LoadEventSummaries(TheirsId));
    }

    /// <summary>
    /// PostgreSQL compares text case-sensitively, so a mapping row whose case differs from the
    /// Keycloak username would otherwise lock the organizer out of their own events.
    /// </summary>
    [TestMethod]
    public async Task AUsernameDifferingOnlyInCase_IsStillTheSameOrganizer()
    {
        await SeedAsync();
        SignIn("Organizer@Example.COM");

        Assert.HasCount(1, await events.LoadEventSummaries(MineId));
    }

    [TestMethod]
    public async Task AnOrganizerCanCreateAnEventInAnOrganizationTheyAdminister()
    {
        await SeedAsync();
        SignIn(Organizer);

        var result = await events.SaveNewEvent(new ConfigEvent { Name = "New" }, MineId);

        var created = db.Events.AsNoTracking().Single(e => e.Name == "New");
        Assert.AreEqual(MineId, created.OrganizationId);
        Assert.IsTrue(result.Value > 0);
    }

    /// <summary>
    /// The create path used to read <c>IsSimulation</c> off the caller's token - <c>client_id</c>
    /// starting with "api", defaulting to true when the claim was absent. A person's token has no
    /// organization client id in it, so every event an organizer created from the web would have
    /// become a simulation with source data logging switched off, and the update path's <c>||</c>
    /// would have made that permanent. It is read from the organization instead.
    /// </summary>
    [TestMethod]
    public async Task AnEventAnOrganizerCreates_IsNotASimulation()
    {
        await SeedAsync();
        SignIn(Organizer);

        await events.SaveNewEvent(new ConfigEvent { Name = "Real" }, MineId);

        var created = db.Events.AsNoTracking().Single(e => e.Name == "Real");
        Assert.IsFalse(created.IsSimulation, "An organizer's event was recorded as a simulation.");
        Assert.IsTrue(created.EnableSourceDataLogging, "Source data logging was left off for a real event.");
    }

    /// <summary>An API organization still produces simulations, whoever asked for the event.</summary>
    [TestMethod]
    public async Task AnEventCreatedForAnApiOrganization_IsStillASimulation()
    {
        await SeedAsync();
        SignIn(Organizer);

        await events.SaveNewEvent(new ConfigEvent { Name = "Synthetic" }, BothId);

        var created = db.Events.AsNoTracking().Single(e => e.Name == "Synthetic");
        Assert.IsTrue(created.IsSimulation);
        Assert.IsFalse(created.EnableSourceDataLogging);
    }

    /// <summary>
    /// A relay organization's event can be taken back out of simulation. The update path used to
    /// re-assert the flag from the token on every save, so a mis-set one could never be corrected.
    /// </summary>
    [TestMethod]
    public async Task AnOrganizerCanClearTheSimulationFlagOnTheirOwnEvent()
    {
        await SeedAsync();
        SignIn(Organizer);
        var evt = db.Events.Single(e => e.Id == 10);
        evt.IsSimulation = true;
        await db.SaveChangesAsync();

        await events.UpdateEvent(new ConfigEvent { Id = 10, Name = "Mine event", IsSimulation = false });

        Assert.IsFalse(db.Events.AsNoTracking().Single(e => e.Id == 10).IsSimulation);
    }

    [TestMethod]
    public async Task AnOrganizerCannotCreateAnEventInSomebodyElsesOrganization()
    {
        await SeedAsync();
        SignIn(Organizer);

        var result = await events.SaveNewEvent(new ConfigEvent { Name = "Intruder" }, TheirsId);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result);
        Assert.IsFalse(db.Events.AsNoTracking().Any(e => e.Name == "Intruder"));
    }

    [TestMethod]
    public async Task AnOrganizerCannotTouchAnEventInSomebodyElsesOrganization()
    {
        await SeedAsync();
        SignIn(Organizer);

        Assert.IsInstanceOfType<NotFoundObjectResult>(
            await events.UpdateEvent(new ConfigEvent { Id = 20, Name = "Hijacked" }));
        Assert.IsInstanceOfType<NotFoundObjectResult>(await events.DeleteEvent(20));
        Assert.IsInstanceOfType<NotFoundObjectResult>(await events.UpdateEventStatusActive(20));

        var untouched = db.Events.AsNoTracking().Single(e => e.Id == 20);
        Assert.AreEqual("Their event", untouched.Name);
        Assert.IsFalse(untouched.IsDeleted);
    }

    /// <summary>
    /// A token carrying neither an organization's client id nor a username identifies nobody, and
    /// has to be refused rather than quietly given an organization.
    /// </summary>
    [TestMethod]
    public async Task ATokenIdentifyingNobody_CreatesNothing()
    {
        await SeedAsync();

        // The row that makes this test bite. Without it the guard could be deleted and everything
        // would still pass, because nothing would match an empty username either way. A blank row is
        // reachable: the administrator list is posted as free text.
        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = "",
            OrganizationId = MineId,
            Role = "admin",
        });
        await db.SaveChangesAsync();

        SignIn(username: null);

        var result = await events.SaveNewEvent(new ConfigEvent { Name = "Anonymous" }, MineId);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result);
        Assert.IsFalse(db.Events.AsNoTracking().Any(e => e.Name == "Anonymous"));
    }

    /// <summary>
    /// A mapping row is membership, not administration. These endpoints hand out Flagtronics and X2
    /// credentials and rewrite the administrator list itself, so a non-admin member reaching them
    /// could promote themselves and remove the incumbents in one request. Before the caller could be
    /// a person at all, only the organization's own machine account could reach them, so nothing had
    /// to distinguish the two.
    /// </summary>
    [TestMethod]
    public async Task ANonAdminMember_CannotAdministerTheOrganization()
    {
        await SeedAsync();
        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = "viewer@example.com",
            OrganizationId = MineId,
            Role = "viewer",
        });
        await db.SaveChangesAsync();
        SignIn("viewer@example.com");

        Assert.IsEmpty(await events.LoadEventSummaries(MineId), "A viewer was shown the organization's events.");
        Assert.IsInstanceOfType<NotFoundObjectResult>(
            (await events.SaveNewEvent(new ConfigEvent { Name = "Viewer event" }, MineId)).Result);
        Assert.IsFalse(db.Events.AsNoTracking().Any(e => e.Name == "Viewer event"));
    }

    /// <summary>Both spellings of the role are in the data, so the comparison cannot be exact.</summary>
    [TestMethod]
    public async Task TheAdminRoleIsMatchedWithoutRegardToCase()
    {
        await SeedAsync();
        var mapping = db.UserOrganizationMappings.Single(m => m.Username == Organizer && m.OrganizationId == MineId);
        mapping.Role = "Admin";
        await db.SaveChangesAsync();
        SignIn(Organizer);

        Assert.HasCount(1, await events.LoadEventSummaries(MineId));
    }

    /// <summary>
    /// Nothing links a mapping row to the organization it names, and the sibling endpoint in
    /// UserManagement documents that dangling rows are expected. Being permitted against an
    /// organization that no longer exists has to be a refusal, not an unhandled exception.
    /// </summary>
    [TestMethod]
    public async Task AMappingToAnOrganizationThatNoLongerExists_IsRefusedRatherThanThrowing()
    {
        await SeedAsync();
        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = Organizer,
            OrganizationId = 99,
            Role = "admin",
        });
        await db.SaveChangesAsync();
        SignIn(Organizer);

        var result = await events.SaveNewEvent(new ConfigEvent { Name = "Orphan" }, 99);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result);
        Assert.IsFalse(db.Events.AsNoTracking().Any(e => e.Name == "Orphan"));
    }

    /// <summary>
    /// Activating an event deactivates the others in its organization with raw SQL. The scope has to
    /// be the event's own organization: an organizer holding two would otherwise have the events of
    /// the other one switched off. The in-memory provider cannot run that statement, so the scope is
    /// asserted at the seam instead - without this, deleting its WHERE clause breaks nothing.
    /// </summary>
    [TestMethod]
    public async Task ActivatingAnEvent_DeactivatesOnlyWithinThatEventsOrganization()
    {
        await SeedAsync();
        SignIn(Organizer);

        await events.UpdateEventStatusActive(10);

        var (organizationId, eventId) = events.Activations.Single();
        Assert.AreEqual(MineId, organizationId,
            "The deactivation was scoped to something other than the event's own organization.");
        Assert.AreEqual(10, eventId);
    }

    [TestMethod]
    public async Task ActivatingAnEventTheCallerDoesNotHold_TouchesNothing()
    {
        await SeedAsync();
        SignIn(Organizer);

        Assert.IsInstanceOfType<NotFoundObjectResult>(await events.UpdateEventStatusActive(20));
        Assert.IsEmpty(events.Activations);
    }

    /// <summary>
    /// A concrete controller that records the active-event scope rather than issuing the raw SQL,
    /// which the in-memory provider cannot execute.
    /// </summary>
    private sealed class OrganizerEventController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        IConnectionMultiplexer cacheMux) : EventControllerBase(loggerFactory, tsContext, cacheMux)
    {
        public List<(int OrganizationId, int EventId)> Activations { get; } = [];

        protected override Task SetActiveEventAsync(TsContext context, int organizationId, int eventId)
        {
            Activations.Add((organizationId, eventId));
            return Task.CompletedTask;
        }
    }
}
