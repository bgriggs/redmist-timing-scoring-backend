using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Controllers;
using RedMist.EventManagement.Models;
using RedMist.EventProcessor.Tests.Utilities;
using System.Security.Claims;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;

namespace RedMist.TimingAndScoringService.Tests.EventManagement.Controllers;

/// <summary>
/// Reading back the viewership the post-event report job produced.
/// </summary>
[TestClass]
public class ViewershipControllerTests
{
    private const string Organizer = "organizer@example.com";
    private const int MineId = 1;
    private const int TheirsId = 2;

    private IDbContextFactory<TsContext> dbFactory = null!;
    private TsContext db = null!;
    private TestViewershipController controller = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db = dbFactory.CreateDbContext();
        controller = new TestViewershipController(loggerFactory.Object, dbFactory);
        SignIn(Organizer);
    }

    [TestCleanup]
    public void Cleanup() => db.Dispose();

    private void SignIn(string? username)
    {
        List<Claim> claims = [new Claim("client_id", "redmist-landing")];
        if (username != null)
        {
            claims.Add(new Claim(ClaimTypes.Name, username));
        }
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType")) }
        };
    }

    private async Task SeedAsync()
    {
        db.Organizations.AddRange(
            new Organization { Id = MineId, ClientId = "relay-mine", Name = "Mine", ShortName = "M" },
            new Organization { Id = TheirsId, ClientId = "relay-theirs", Name = "Theirs", ShortName = "T" });
        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = Organizer,
            OrganizationId = MineId,
            Role = "admin",
        });
        db.Events.AddRange(
            NewEvent(10, MineId, "Spring Classic", new DateTime(2026, 3, 1)),
            NewEvent(11, MineId, "Autumn Classic", new DateTime(2026, 9, 1)),
            NewEvent(20, TheirsId, "Their event", new DateTime(2026, 5, 1)));
        await db.SaveChangesAsync();
    }

    private static ConfigEvent NewEvent(int id, int orgId, string name, DateTime start) => new()
    {
        Id = id,
        OrganizationId = orgId,
        Name = name,
        StartDate = start,
        EndDate = start.AddDays(1),
    };

    /// <summary>Adds a report, with viewership unless <paramref name="withViewership"/> is false.</summary>
    private async Task SeedReportAsync(int eventId, int organizationId, bool withViewership = true,
        int maxConcurrent = 12, string state = PostEventReportState.Sent)
    {
        var report = new PostEventReport
        {
            EventId = eventId,
            OrganizationId = organizationId,
            GeneratedUtc = new DateTime(2026, 9, 3, 6, 30, 0, DateTimeKind.Utc),
            State = state,
        };
        if (withViewership)
        {
            report.Viewership = new EventViewershipSummary
            {
                WindowStartUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                WindowEndUtc = new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc),
                TrackOffsetMinutes = -300,
                TotalViewerMinutes = 480.5,
                MaxConcurrent = maxConcurrent,
                PeakUtc = new DateTime(2026, 9, 1, 14, 30, 0, DateTimeKind.Utc),
                TopClientType = "Web",
                SessionCount = 40,
                OpenSessions = 2,
                AnomalousSessions = 1,
                Sessions =
                [
                    new EventViewershipSessionSummary
                    {
                        SessionId = 2, SessionName = "Race", IsPracticeQualifying = false,
                        StartUtc = new DateTime(2026, 9, 1, 15, 0, 0, DateTimeKind.Utc),
                        EndUtc = new DateTime(2026, 9, 1, 17, 0, 0, DateTimeKind.Utc),
                        TotalViewerMinutes = 300, MaxConcurrent = 12, TopClientType = "Web",
                    },
                    new EventViewershipSessionSummary
                    {
                        SessionId = 1, SessionName = "Practice", IsPracticeQualifying = true,
                        StartUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                        EndUtc = new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Utc),
                        TotalViewerMinutes = 120, MaxConcurrent = 5, TopClientType = "iOS",
                    },
                ],
                Buckets =
                [
                    NewBucket(null, new DateTime(2026, 9, 1, 12, 15, 0, DateTimeKind.Utc), "All", 9),
                    NewBucket(null, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), "All", 4),
                    NewBucket(1, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), "Web", 3),
                ],
            };
        }
        db.PostEventReports.Add(report);
        await db.SaveChangesAsync();
    }

    private static EventViewershipBucket NewBucket(int? sessionId, DateTime start, string clientType, int max)
        => new()
        {
            SessionId = sessionId,
            BucketStartUtc = start,
            ClientType = clientType,
            MinConcurrent = 0,
            MaxConcurrent = max,
            AvgConcurrent = max / 2.0,
            ViewerSeconds = max * 450,
        };

    #region Reports

    [TestMethod]
    public async Task AnOrganizerSeesTheirOwnReports_NewestEventFirst()
    {
        await SeedAsync();
        await SeedReportAsync(10, MineId);
        await SeedReportAsync(11, MineId);

        var reports = (await controller.Reports(MineId)).Value!;

        CollectionAssert.AreEqual(new[] { 11, 10 }, reports.Select(r => r.EventId).ToArray());
        Assert.AreEqual("Autumn Classic", reports[0].EventName);
        Assert.AreEqual(12, reports[0].MaxConcurrent);
        Assert.AreEqual(-300, reports[0].TrackOffsetMinutes);
    }

    /// <summary>
    /// A report row exists for every event the job processed, including ones it suppressed or had
    /// nothing to say about. Listing those would show an organizer rows with no numbers behind them.
    /// </summary>
    [TestMethod]
    public async Task AReportWithNoViewership_IsNotListed()
    {
        await SeedAsync();
        await SeedReportAsync(10, MineId, withViewership: false, state: PostEventReportState.NoContent);
        await SeedReportAsync(11, MineId);

        var reports = (await controller.Reports(MineId)).Value!;

        CollectionAssert.AreEqual(new[] { 11 }, reports.Select(r => r.EventId).ToArray());
    }

    [TestMethod]
    public async Task AnotherOrganizationsReports_AreNotListed()
    {
        await SeedAsync();
        await SeedReportAsync(20, TheirsId);

        Assert.IsEmpty((await controller.Reports(TheirsId)).Value!);
    }

    [TestMethod]
    public async Task ANonAdminMember_SeesNoReports()
    {
        await SeedAsync();
        await SeedReportAsync(10, MineId);
        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = "viewer@example.com",
            OrganizationId = MineId,
            Role = "viewer",
        });
        await db.SaveChangesAsync();
        SignIn("viewer@example.com");

        Assert.IsEmpty((await controller.Reports(MineId)).Value!);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task AnOrganizationIdThatCannotExist_IsRefused(int organizationId)
    {
        await SeedAsync();

        Assert.IsInstanceOfType<BadRequestObjectResult>((await controller.Reports(organizationId)).Result);
    }

    [TestMethod]
    public async Task PagingWalksTheReportsWithoutRepeatingOne()
    {
        await SeedAsync();
        await SeedReportAsync(10, MineId);
        await SeedReportAsync(11, MineId);

        var first = (await controller.Reports(MineId, skip: 0, take: 1)).Value!;
        var second = (await controller.Reports(MineId, skip: 1, take: 1)).Value!;

        Assert.AreEqual(11, first.Single().EventId);
        Assert.AreEqual(10, second.Single().EventId);
    }

    /// <summary>
    /// A caller cannot ask for an unbounded page by naming a very large one. Seeded past the cap on
    /// purpose: with one report the answer is one row whether the cap exists or not, so the test
    /// would pass with Take(take) and prove nothing.
    /// </summary>
    [TestMethod]
    public async Task TakeIsCapped()
    {
        await SeedAsync();
        for (var i = 0; i < 105; i++)
        {
            db.Events.Add(NewEvent(100 + i, MineId, $"Event {i}", new DateTime(2026, 1, 1).AddDays(i)));
        }
        await db.SaveChangesAsync();
        for (var i = 0; i < 105; i++)
        {
            await SeedReportAsync(100 + i, MineId);
        }

        Assert.HasCount(100, (await controller.Reports(MineId, take: 100_000)).Value!);
    }

    [TestMethod]
    [DataRow(-1, 20)]
    [DataRow(0, 0)]
    public async Task NonsensePaging_IsRefused(int skip, int take)
    {
        await SeedAsync();

        Assert.IsInstanceOfType<BadRequestObjectResult>((await controller.Reports(MineId, skip, take)).Result);
    }

    /// <summary>
    /// Deleted events are excluded everywhere else in this service, and an organizer who deleted an
    /// event should not still find it listed here.
    /// </summary>
    [TestMethod]
    public async Task ADeletedEventsReport_IsNotListedAndCannotBeOpened()
    {
        await SeedAsync();
        await SeedReportAsync(11, MineId);
        db.Events.Single(e => e.Id == 11).IsDeleted = true;
        await db.SaveChangesAsync();

        Assert.IsEmpty((await controller.Reports(MineId)).Value!);
        Assert.IsInstanceOfType<NotFoundResult>((await controller.Report(11)).Result);
    }

    #endregion

    #region Report

    [TestMethod]
    public async Task AReportCarriesItsSessionsAndBuckets()
    {
        await SeedAsync();
        await SeedReportAsync(11, MineId);

        var report = (await controller.Report(11)).Value!;

        Assert.AreEqual(11, report.Summary.EventId);
        Assert.AreEqual("Autumn Classic", report.Summary.EventName);
        // Sessions in the order they ran, not the order they were stored.
        CollectionAssert.AreEqual(new[] { "Practice", "Race" }, report.Sessions.Select(s => s.SessionName).ToArray());
        Assert.HasCount(3, report.Buckets);
    }

    /// <summary>
    /// Ordered so a caller can walk them straight into a series: the event-level series first, then
    /// each session's, each in time order.
    /// </summary>
    [TestMethod]
    public async Task BucketsComeBackOrderedForPlotting()
    {
        await SeedAsync();
        await SeedReportAsync(11, MineId);

        var buckets = (await controller.Report(11)).Value!.Buckets;

        Assert.IsNull(buckets[0].SessionId);
        Assert.IsNull(buckets[1].SessionId);
        Assert.IsTrue(buckets[0].BucketStartUtc < buckets[1].BucketStartUtc,
            "The event-level series was not in time order.");
        Assert.AreEqual(1, buckets[2].SessionId);
    }

    /// <summary>
    /// A report belonging to somebody else and a report that does not exist are the same answer on
    /// purpose: telling them apart would confirm that another organization's event exists.
    /// </summary>
    [TestMethod]
    public async Task AnotherOrganizationsReport_IsIndistinguishableFromAMissingOne()
    {
        await SeedAsync();
        await SeedReportAsync(20, TheirsId);

        var theirs = await controller.Report(20);
        var missing = await controller.Report(999);

        Assert.IsInstanceOfType<NotFoundResult>(theirs.Result);
        Assert.IsInstanceOfType<NotFoundResult>(missing.Result);
    }

    [TestMethod]
    public async Task AReportWithNoViewership_IsNotFound()
    {
        await SeedAsync();
        await SeedReportAsync(10, MineId, withViewership: false, state: PostEventReportState.NoContent);

        Assert.IsInstanceOfType<NotFoundResult>((await controller.Report(10)).Result);
    }

    [TestMethod]
    public async Task ATokenIdentifyingNobody_ReadsNothing()
    {
        await SeedAsync();
        await SeedReportAsync(11, MineId);
        SignIn(username: null);

        Assert.IsInstanceOfType<NotFoundResult>((await controller.Report(11)).Result);
    }

    #endregion

    /// <summary>
    /// The feed emits a genuine session id 0, so the event-level series cannot be ordered by
    /// coalescing null onto 0 - that would interleave the two and hand a caller one series made of
    /// two.
    /// </summary>
    [TestMethod]
    public async Task ASessionNumberedZero_DoesNotInterleaveWithTheEventLevelSeries()
    {
        await SeedAsync();
        await SeedReportAsync(11, MineId);
        var viewership = db.PostEventReports.Include(r => r.Viewership!).ThenInclude(v => v.Buckets)
            .Single(r => r.EventId == 11).Viewership!;
        viewership.Buckets.Add(NewBucket(0, new DateTime(2026, 9, 1, 12, 5, 0, DateTimeKind.Utc), "All", 7));
        await db.SaveChangesAsync();

        var buckets = (await controller.Report(11)).Value!.Buckets;

        // Every event-level bucket comes before every session bucket, session 0 included.
        var firstSessionBucket = buckets.FindIndex(b => b.SessionId.HasValue);
        Assert.IsTrue(buckets.Take(firstSessionBucket).All(b => b.SessionId == null));
        Assert.IsTrue(buckets.Skip(firstSessionBucket).All(b => b.SessionId.HasValue),
            "A session's buckets were spliced into the event-level series.");
    }

    private sealed class TestViewershipController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
        : ViewershipControllerBase(loggerFactory, tsContext);
}
