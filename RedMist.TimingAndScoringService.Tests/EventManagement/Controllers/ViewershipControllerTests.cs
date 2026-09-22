using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Controllers;
using RedMist.EventManagement.Models;
using RedMist.EventProcessor.Tests.Utilities;
using System.Security.Claims;
using System.Text.Json;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;
using Session = RedMist.TimingCommon.Models.Session;

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

    /// <summary>During event 11, which runs on 2026-09-01.</summary>
    private static readonly DateTime Now = new(2026, 9, 1, 16, 0, 0, DateTimeKind.Utc);

    private IDbContextFactory<TsContext> dbFactory = null!;
    private TsContext db = null!;
    private Mock<ILoggerFactory> loggerFactory = null!;
    private FakeHybridCache cache = null!;
    private FakeTimeProvider clock = null!;
    private TestViewershipController controller = null!;

    [TestInitialize]
    public void Setup()
    {
        loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db = dbFactory.CreateDbContext();
        cache = new FakeHybridCache();
        clock = new FakeTimeProvider(Now);
        controller = new TestViewershipController(loggerFactory.Object, dbFactory, cache, clock);
        SignIn(Organizer);
    }

    [TestCleanup]
    public void Cleanup() => db.Dispose();

    private void SignIn(string? username) => SignIn(controller, username);

    private static void SignIn(ControllerBase controller, string? username, string clientId = "redmist-landing")
    {
        List<Claim> claims = [new Claim("client_id", clientId)];
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

    #region ReportStatus

    /// <summary>A week after event 11 ended, inside the job's lookback.</summary>
    private static readonly DateTime StatusDay = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Moves the clock the report status reads past the seeded events. It reads the injected clock
    /// rather than the machine's, so that what it calls finished and eligible can be pinned to the
    /// same instant the report job is asked at.
    /// </summary>
    private void AfterTheEvents() => clock.SetUtcNow(new DateTimeOffset(StatusDay));

    /// <summary>
    /// The three states a page has to tell apart. The middle one is the reason this endpoint exists:
    /// an event nobody watched is processed and finished, and rendering it as "no report yet" would
    /// leave an organizer waiting for something that is never coming.
    /// </summary>
    [TestMethod]
    public async Task ReportStatus_DistinguishesNotYetProcessedFromNothingToReport()
    {
        await SeedAsync();
        AfterTheEvents();
        await SeedReportAsync(10, MineId, withViewership: false, state: PostEventReportState.NoContent);
        await SeedReportAsync(11, MineId);
        db.Events.AddRange(NewEvent(12, MineId, "Unprocessed", new DateTime(2026, 2, 1)));
        await db.SaveChangesAsync();

        var statuses = (await controller.ReportStatus(MineId)).Value!.ToDictionary(x => x.EventId);

        Assert.IsNull(statuses[12].State, "An unprocessed event did not read as unprocessed.");
        Assert.AreEqual(PostEventReportState.NoContent, statuses[10].State);
        Assert.AreEqual(PostEventReportState.Sent, statuses[11].State);
    }

    /// <summary>
    /// The events needing "no report yet" are exactly the ones absent from the reports list, so a
    /// caller with only ids would have to visit another service purely to label them.
    /// </summary>
    [TestMethod]
    public async Task ReportStatus_CarriesEnoughToRenderWithoutASecondCall()
    {
        await SeedAsync();
        AfterTheEvents();

        var status = (await controller.ReportStatus(MineId)).Value!.Single(x => x.EventId == 11);

        Assert.AreEqual("Autumn Classic", status.EventName);
        Assert.AreEqual(new DateTime(2026, 9, 2), status.EventEndDate);
    }

    [TestMethod]
    public async Task ReportStatus_ExcludesEventsThatHaveNotFinished()
    {
        await SeedAsync();
        AfterTheEvents();
        db.Events.Add(NewEvent(13, MineId, "Next month", StatusDay.AddDays(30)));
        await db.SaveChangesAsync();

        var ids = (await controller.ReportStatus(MineId)).Value!.Select(x => x.EventId).ToArray();

        CollectionAssert.DoesNotContain(ids, 13);
    }

    [TestMethod]
    public async Task ReportStatus_ExcludesDeletedEventsAndOtherOrganizations()
    {
        await SeedAsync();
        AfterTheEvents();
        db.Events.Single(e => e.Id == 10).IsDeleted = true;
        await db.SaveChangesAsync();

        var mine = (await controller.ReportStatus(MineId)).Value!.Select(x => x.EventId).ToArray();

        CollectionAssert.DoesNotContain(mine, 10);
        CollectionAssert.DoesNotContain(mine, 20);
        Assert.IsEmpty((await controller.ReportStatus(TheirsId)).Value!);
    }

    /// <summary>
    /// A null state has to mean "not yet", which it cannot for an event the job will never look at.
    /// Without this, every event from before the job's lookback window reads as pending forever, and
    /// a page listing last season shows nothing but rows waiting for reports that are never coming.
    /// </summary>
    [TestMethod]
    public async Task ReportStatus_SaysWhenTheJobWillNeverReachAnEvent()
    {
        await SeedAsync();
        AfterTheEvents();
        db.Events.AddRange(
            NewEvent(30, MineId, "Recent", StatusDay.AddDays(-3)),
            NewEvent(31, MineId, "Last season", StatusDay.AddDays(-200)));
        await db.SaveChangesAsync();

        var statuses = (await controller.ReportStatus(MineId)).Value!.ToDictionary(x => x.EventId);

        Assert.IsNull(statuses[30].State);
        Assert.IsTrue(statuses[30].Eligible, "A recent unprocessed event was reported as never coming.");
        Assert.IsNull(statuses[31].State);
        Assert.IsFalse(statuses[31].Eligible, "An event past the job's lookback was reported as still pending.");
    }

    /// <summary>The job never processes these, so listing them could only ever say "pending" about them.</summary>
    [TestMethod]
    public async Task ReportStatus_OmitsSimulationsAndEventsStillFlaggedLive()
    {
        await SeedAsync();
        AfterTheEvents();
        var sim = NewEvent(32, MineId, "Load test", StatusDay.AddDays(-3));
        sim.IsSimulation = true;
        var stuck = NewEvent(33, MineId, "Stuck live", StatusDay.AddDays(-3));
        stuck.IsLive = true;
        db.Events.AddRange(sim, stuck);
        await db.SaveChangesAsync();

        var ids = (await controller.ReportStatus(MineId)).Value!.Select(x => x.EventId).ToArray();

        CollectionAssert.DoesNotContain(ids, 32);
        CollectionAssert.DoesNotContain(ids, 33);
    }

    [TestMethod]
    public async Task ReportStatus_IsPagedLikeTheReportsList()
    {
        await SeedAsync();
        AfterTheEvents();

        var first = (await controller.ReportStatus(MineId, skip: 0, take: 1)).Value!;
        var second = (await controller.ReportStatus(MineId, skip: 1, take: 1)).Value!;

        Assert.AreEqual(11, first.Single().EventId);
        Assert.AreEqual(10, second.Single().EventId);
        Assert.IsInstanceOfType<BadRequestObjectResult>((await controller.ReportStatus(MineId, take: 0)).Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>((await controller.ReportStatus(0)).Result);
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

    #region Live

    /// <summary>
    /// Makes event 11 live, with a running race and some viewers on it. Timestamps are stored with no
    /// Kind, which is how PostgreSQL returns these columns.
    /// </summary>
    private async Task SeedLiveAsync()
    {
        static DateTime Unspecified(int hour, int minute) => new(2026, 9, 1, hour, minute, 0, DateTimeKind.Unspecified);

        db.Events.Single(e => e.Id == 11).IsLive = true;
        db.Sessions.AddRange(
            new Session
            {
                Id = 1, EventId = 11, Name = "Practice", IsPracticeQualifying = true,
                StartTime = Unspecified(14, 0), EndTime = Unspecified(14, 45), LocalTimeZoneOffset = -4,
            },
            new Session
            {
                Id = 2, EventId = 11, Name = "Race", StartTime = Unspecified(15, 0), IsLive = true,
                LocalTimeZoneOffset = -4,
            });
        db.EventViewerSessions.AddRange(
            NewViewer(11, Unspecified(14, 5), Unspecified(14, 40)),
            NewViewer(11, Unspecified(14, 50), null),
            NewViewer(11, Unspecified(15, 10), Unspecified(15, 30)),
            // Another event's viewer, who must not appear in this one's numbers.
            NewViewer(20, Unspecified(15, 0), null));
        await db.SaveChangesAsync();
    }

    private static EventViewerSession NewViewer(int eventId, DateTime start, DateTime? end) => new()
    {
        EventId = eventId,
        ConnectionId = Guid.NewGuid().ToString(),
        ClientType = "Web",
        StartUtc = start,
        EndUtc = end,
    };

    [TestMethod]
    public async Task Live_AnOrganizerSeesTheirRunningEvent()
    {
        await SeedAsync();
        await SeedLiveAsync();

        var live = (await controller.Live(11)).Value!;

        Assert.AreEqual(11, live.EventId);
        Assert.AreEqual(Now, live.AsOfUtc);
        Assert.AreEqual(60, live.BucketSeconds);
        Assert.AreEqual(-240, live.TrackOffsetMinutes);
        CollectionAssert.AreEqual(new[] { "Practice", "Race" }, live.Sessions.Select(s => s.SessionName).ToArray());
        Assert.IsNull(live.Sessions[1].EndUtc, "The race is still running.");
        // 35 + 70 + 20 minutes: the open row counted to now, the other event's viewer not at all.
        Assert.AreEqual((35 + 70 + 20) * 60, live.Event.TotalViewerSeconds);
        Assert.AreEqual(2, live.Event.MaxConcurrent);
    }

    /// <summary>
    /// The same rule as <see cref="ViewershipControllerBase.Report"/>: an event belonging to somebody
    /// else and an event that does not exist are the same answer, so the difference cannot be used to
    /// confirm that another organization's event exists.
    /// </summary>
    [TestMethod]
    public async Task Live_AnotherOrganizationsEvent_IsIndistinguishableFromAMissingOne()
    {
        await SeedAsync();
        await SeedLiveAsync();

        Assert.IsInstanceOfType<NotFoundResult>((await controller.Live(20)).Result);
        Assert.IsInstanceOfType<NotFoundResult>((await controller.Live(999)).Result);
        Assert.IsEmpty(cache.FactoryInvocations, "Something was computed for an event the caller may not read.");
    }

    [TestMethod]
    public async Task Live_ANonAdminMember_IsNotFound()
    {
        await SeedAsync();
        await SeedLiveAsync();
        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = "viewer@example.com",
            OrganizationId = MineId,
            Role = "viewer",
        });
        await db.SaveChangesAsync();
        SignIn("viewer@example.com");

        Assert.IsInstanceOfType<NotFoundResult>((await controller.Live(11)).Result);
    }

    /// <summary>
    /// A relay or API client authenticates as its organization's own client id, with no person
    /// behind it. It reads its own organization's live viewership and nobody else's.
    /// </summary>
    [TestMethod]
    public async Task Live_AnOrganizationsOwnClient_ReadsItsEventsAndNoOneElses()
    {
        await SeedAsync();
        await SeedLiveAsync();

        SignIn(controller, username: null, clientId: "relay-mine");
        Assert.AreEqual(11, (await controller.Live(11)).Value!.EventId);

        SignIn(controller, username: null, clientId: "relay-theirs");
        Assert.IsInstanceOfType<NotFoundResult>((await controller.Live(11)).Result);
    }

    [TestMethod]
    public async Task Live_ATokenIdentifyingNobody_ReadsNothing()
    {
        await SeedAsync();
        await SeedLiveAsync();
        SignIn(username: null);

        Assert.IsInstanceOfType<NotFoundResult>((await controller.Live(11)).Result);
    }

    [TestMethod]
    public async Task Live_ADeletedEvent_IsNotFound()
    {
        await SeedAsync();
        await SeedLiveAsync();
        db.Events.Single(e => e.Id == 11).IsDeleted = true;
        await db.SaveChangesAsync();

        Assert.IsInstanceOfType<NotFoundResult>((await controller.Live(11)).Result);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task Live_AnEventIdThatCannotExist_IsRefused(int eventId)
    {
        await SeedAsync();

        Assert.IsInstanceOfType<BadRequestObjectResult>((await controller.Live(eventId)).Result);
    }

    /// <summary>The whole controller refuses anonymous callers, the new action included.</summary>
    [TestMethod]
    public void Live_RequiresAnAuthenticatedCaller()
    {
        var authorize = typeof(ViewershipControllerBase)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true);
        var anonymous = typeof(ViewershipControllerBase).GetMethod(nameof(ViewershipControllerBase.Live))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true);

        Assert.IsNotEmpty(authorize);
        Assert.IsEmpty(anonymous);
    }

    /// <summary>
    /// Every dashboard on the event shares one computation until the answer is refreshed. A viewer
    /// arriving in between does not appear until then; that is the cost of not reading every row the
    /// event has on every poll.
    /// </summary>
    [TestMethod]
    public async Task Live_IsServedFromTheCacheBetweenRefreshes()
    {
        await SeedAsync();
        await SeedLiveAsync();

        var first = (await controller.Live(11)).Value!;
        db.EventViewerSessions.Add(NewViewer(11, new DateTime(2026, 9, 1, 15, 55, 0), null));
        await db.SaveChangesAsync();
        var second = (await controller.Live(11)).Value!;

        Assert.HasCount(1, cache.FactoryInvocations);
        Assert.AreEqual(first.Event.TotalViewerSeconds, second.Event.TotalViewerSeconds);
    }

    /// <summary>
    /// The dashboard polls again the moment the session changes, and that is the poll that most needs
    /// to be fresh. Served from before the change, the new session would not appear until the next
    /// regular poll a minute later.
    /// </summary>
    [TestMethod]
    public async Task Live_ASessionChange_IsNotHiddenByTheCache()
    {
        await SeedAsync();
        await SeedLiveAsync();
        await controller.Live(11);

        // The race is retired and a second race begins, as the timing processor does it.
        var race = db.Sessions.Single(s => s.EventId == 11 && s.Id == 2);
        race.IsLive = false;
        race.EndTime = new DateTime(2026, 9, 1, 15, 50, 0);
        db.Sessions.Add(new Session
        {
            Id = 3, EventId = 11, Name = "Race 2", StartTime = new DateTime(2026, 9, 1, 15, 55, 0), IsLive = true,
            LocalTimeZoneOffset = -4,
        });
        await db.SaveChangesAsync();

        var live = (await controller.Live(11)).Value!;

        Assert.HasCount(2, cache.FactoryInvocations);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, live.Sessions.Select(s => s.SessionId).ToArray());
        Assert.IsNull(live.Sessions[2].EndUtc);
    }

    /// <summary>
    /// The orchestrator tearing the event down is what finally ends a session whose own flag stuck,
    /// so it has to reach the dashboard as promptly as a session ending does.
    /// </summary>
    [TestMethod]
    public async Task Live_TheEventGoingOffline_IsNotHiddenByTheCache()
    {
        await SeedAsync();
        await SeedLiveAsync();
        Assert.IsNull((await controller.Live(11)).Value!.Sessions[1].EndUtc);

        db.Events.Single(e => e.Id == 11).IsLive = false;
        await db.SaveChangesAsync();

        Assert.IsNotNull((await controller.Live(11)).Value!.Sessions[1].EndUtc,
            "The race was still reported as running after the event went offline.");
    }

    /// <summary>
    /// Two events whose sessions happen to be in the same state must not share an answer. Seeded so
    /// that everything but the event id is alike, which is what the cache key has to tell apart.
    /// </summary>
    [TestMethod]
    public async Task Live_EachEventIsCachedSeparately()
    {
        await SeedAsync();
        await SeedLiveAsync();
        static DateTime March(int hour, int minute) => new(2026, 3, 1, hour, minute, 0);
        db.Events.Single(e => e.Id == 10).IsLive = true;
        db.Sessions.AddRange(
            new Session { Id = 1, EventId = 10, Name = "Practice", StartTime = March(14, 0), EndTime = March(14, 45) },
            new Session { Id = 2, EventId = 10, Name = "Race", StartTime = March(15, 0), IsLive = true });
        db.EventViewerSessions.Add(NewViewer(10, March(15, 0), March(15, 5)));
        await db.SaveChangesAsync();

        var theirs = (await controller.Live(10)).Value!;
        var ours = (await controller.Live(11)).Value!;

        Assert.AreEqual(10, theirs.EventId);
        Assert.AreEqual(11, ours.EventId, "Event 11 was answered from event 10's cache entry.");
        Assert.AreEqual(300, theirs.Event.TotalViewerSeconds);
        Assert.AreNotEqual(theirs.Event.TotalViewerSeconds, ours.Event.TotalViewerSeconds);
        Assert.HasCount(2, cache.FactoryInvocations);
    }

    [TestMethod]
    public async Task Live_TheLatestSessionEnding_IsNotHiddenByTheCache()
    {
        await SeedAsync();
        await SeedLiveAsync();
        Assert.IsNull((await controller.Live(11)).Value!.Sessions[1].EndUtc);

        var race = db.Sessions.Single(s => s.EventId == 11 && s.Id == 2);
        race.EndTime = new DateTime(2026, 9, 1, 15, 50, 0);
        await db.SaveChangesAsync();

        Assert.AreEqual(new DateTime(2026, 9, 1, 15, 50, 0), (await controller.Live(11)).Value!.Sessions[1].EndUtc);
    }

    /// <summary>
    /// Every timestamp goes out with its "Z", including after a trip through the real cache, which
    /// stores a serialized copy and hands back a deserialized one. A value that lost its Kind on that
    /// round trip would be written as local time on every response but the first.
    /// </summary>
    [TestMethod]
    public async Task Live_TimestampsAreWrittenAsUtc_IncludingWhenServedFromTheCache()
    {
        await SeedAsync();
        await SeedLiveAsync();
        var realCache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider()
            .GetRequiredService<HybridCache>();
        var cached = new TestViewershipController(loggerFactory.Object, dbFactory, realCache, clock);
        SignIn(cached, Organizer);

        var computed = (await cached.Live(11)).Value!;

        // A viewer the second answer would include if it were computed rather than cached. Without
        // it both answers are computed from the same rows at the same fake time, and would match
        // whether or not the cache was ever hit.
        db.EventViewerSessions.Add(NewViewer(11, new DateTime(2026, 9, 1, 15, 40, 0), null));
        await db.SaveChangesAsync();

        var fromCache = (await cached.Live(11)).Value!;

        foreach (var live in new[] { computed, fromCache })
        {
            Assert.IsTrue(LiveViewershipWireFormat.AllDateTimes(live).All(t => t.Kind == DateTimeKind.Utc));
            LiveViewershipWireFormat.AssertEveryTimestampHasZ(JsonSerializer.Serialize(live, LiveViewershipWireFormat.WebOptions));
        }
        Assert.AreEqual(
            JsonSerializer.Serialize(computed, LiveViewershipWireFormat.WebOptions),
            JsonSerializer.Serialize(fromCache, LiveViewershipWireFormat.WebOptions),
            "The second answer was not the cached copy of the first.");
    }

    /// <summary>
    /// Pins the field names the dashboard was built against. A renamed or missing field fails here
    /// rather than as a blank chart.
    /// </summary>
    [TestMethod]
    public async Task Live_WireFormat_IsTheAgreedShape()
    {
        await SeedAsync();
        await SeedLiveAsync();

        var json = JsonSerializer.Serialize((await controller.Live(11)).Value!, LiveViewershipWireFormat.WebOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var session = root.GetProperty("sessions")[1];

        CollectionAssert.AreEquivalent(
            new[] { "eventId", "asOfUtc", "bucketSeconds", "trackOffsetMinutes", "event", "sessions" },
            Names(root));
        CollectionAssert.AreEquivalent(
            new[] { "windowStartUtc", "totalViewerSeconds", "maxConcurrent", "peakUtc", "avgConcurrentDuringSessions" },
            Names(root.GetProperty("event")));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "sessionId", "sessionName", "isPracticeQualifying", "startUtc", "endUtc", "totalViewerSeconds",
                "maxConcurrent", "peakUtc", "avgConcurrent", "buckets",
            },
            Names(session));
        CollectionAssert.AreEquivalent(new[] { "startUtc", "min", "max", "avg" }, Names(session.GetProperty("buckets")[0]));

        Assert.AreEqual(JsonValueKind.Null, session.GetProperty("endUtc").ValueKind, "A running session's end is null.");
        Assert.AreEqual(60, root.GetProperty("bucketSeconds").GetInt32());

        static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];
    }

    #endregion

    private sealed class TestViewershipController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        HybridCache hcache, TimeProvider clock)
        : ViewershipControllerBase(loggerFactory, tsContext, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            hcache, clock);
}
