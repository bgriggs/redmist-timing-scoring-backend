using BigMission.TestHelpers.Testing;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Controllers.V1;
using RedMist.EventManagement.Models;
using RedMist.EventManagement.Viewership;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.PostEventReports;
using RedMist.PostEventReports.Sections;
using RedMist.PostEventReports.Sections.Viewership;
using RedMist.PostEventReports.Suggestions;
using RedMist.TimingCommon.Models;
using System.Security.Claims;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.PostEventReports;

/// <summary>
/// Covers which events get a report, who receives it, and what is recorded about the attempt.
/// </summary>
[TestClass]
public class PostEventReportJobTests
{
    private const int EventId = 400;
    private const int OrganizationId = 7;
    private static readonly DateTime Now = new(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc);

    private TestDbContextFactory dbFactory = null!;
    private FakeTimeProvider clock = null!;
    private Mock<IHostApplicationLifetime> lifetime = null!;

    [TestInitialize]
    public void Setup()
    {
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"PostEventReportJobTests_{Guid.NewGuid()}")
            .Options);
        clock = new FakeTimeProvider(new DateTimeOffset(Now));

        lifetime = new Mock<IHostApplicationLifetime>();
        var started = new CancellationTokenSource();
        started.Cancel();
        lifetime.SetupGet(l => l.ApplicationStarted).Returns(started.Token);
    }

    /// <summary>Captures what would have been sent instead of opening a socket.</summary>
    private sealed class RecordingJob(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> contextFactory,
        EmailHelper emailHelper, IHostApplicationLifetime lifetime, PostEventReportSettings settings,
        IEnumerable<IReportSection> sections, SuggestionEngine suggestions, TimeProvider clock)
        : PostEventReportJob(loggerFactory, contextFactory, emailHelper, lifetime, settings, sections, suggestions, clock)
    {
        public List<(string Subject, string Html, string To, string? Bcc)> Sent { get; } = [];
        public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task SendEmailAsync(string subject, string bodyHtml, string to, string from, string? bcc)
        {
            if (FailFor.Contains(to))
            {
                throw new InvalidOperationException($"SMTP refused {to}");
            }

            Sent.Add((subject, bodyHtml, to, bcc));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A second line of defence: even a path that bypasses the send seam cannot reach a mail server.
    /// </summary>
    private sealed class UnconnectedEmailHelper(IConfiguration configuration) : EmailHelper(configuration)
    {
        protected override ISmtpClient CreateSmtpClient() => Mock.Of<ISmtpClient>();
    }

    private static EmailHelper Emailer() => new UnconnectedEmailHelper(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Host"] = "localhost",
            ["Email:Port"] = "587",
            ["Email:Username"] = "u",
            ["Email:Password"] = "p",
        }).Build());

    private RecordingJob CreateJob(PostEventReportSettings? settings = null, params IReportSection[] sections)
    {
        var loggerFactory = new DebugLoggerFactory();
        settings ??= new PostEventReportSettings();
        IReportSection[] resolved = sections.Length > 0 ? sections : [new ViewershipSection(settings)];
        return new RecordingJob(loggerFactory, dbFactory, Emailer(), lifetime.Object, settings,
            resolved, new SuggestionEngine(loggerFactory, []), clock);
    }

    private void SeedOrganization(string name = "Test Org")
    {
        using var db = dbFactory.CreateDbContext();
        db.Organizations.Add(new Organization { Id = OrganizationId, ClientId = "relay-test", Name = name, ShortName = "TO" });
        db.SaveChanges();
    }

    private void SeedEvent(int id = EventId, Action<Event>? configure = null)
    {
        using var db = dbFactory.CreateDbContext();
        var evt = new Event
        {
            Id = id,
            OrganizationId = OrganizationId,
            Name = "Autumn Classic",
            StartDate = Now.AddDays(-3),
            EndDate = Now.AddDays(-2),
        };
        configure?.Invoke(evt);
        db.Events.Add(evt);
        db.SaveChanges();
    }

    private void SeedAdmins(params string[] usernames)
    {
        using var db = dbFactory.CreateDbContext();
        foreach (var username in usernames)
        {
            db.UserOrganizationMappings.Add(new UserOrganizationMapping
            {
                Username = username,
                OrganizationId = OrganizationId,
                Role = "admin",
            });
        }
        db.SaveChanges();
    }

    /// <summary>Enough viewing for the section to have something to say.</summary>
    private void SeedViewing(int eventId = EventId, int viewers = 3)
    {
        using var db = dbFactory.CreateDbContext();
        var start = Now.AddDays(-2).AddHours(-4);
        for (var i = 0; i < viewers; i++)
        {
            db.EventViewerSessions.Add(new EventViewerSession
            {
                EventId = eventId,
                ConnectionId = $"conn-{eventId}-{i}",
                ClientType = i % 2 == 0 ? "Android" : "iOS",
                StartUtc = start.AddMinutes(i * 5),
                EndUtc = start.AddMinutes(60 + (i * 5)),
            });
        }
        db.SaveChanges();
    }

    private void SeedViewer(DateTime start, DateTime? end, int eventId = EventId)
    {
        using var db = dbFactory.CreateDbContext();
        db.EventViewerSessions.Add(new EventViewerSession
        {
            EventId = eventId,
            ConnectionId = Guid.NewGuid().ToString(),
            ClientType = "Web",
            StartUtc = start,
            EndUtc = end,
        });
        db.SaveChanges();
    }

    /// <summary>The viewership the report recorded for an event, or null when it recorded none.</summary>
    private EventViewershipSummary? Viewership(int eventId = EventId)
    {
        using var db = dbFactory.CreateDbContext();
        return db.PostEventReports.Include(r => r.Viewership)
            .Single(r => r.EventId == eventId).Viewership;
    }

    private async Task<List<int>> CandidatesAsync(PostEventReportSettings? settings = null)
    {
        var job = CreateJob(settings);
        using var db = dbFactory.CreateDbContext();
        return [.. (await job.LoadCandidateEventsAsync(db, CancellationToken.None)).Select(e => e.Id)];
    }

    private List<PostEventReport> Reports()
    {
        using var db = dbFactory.CreateDbContext();
        return [.. db.PostEventReports.OrderBy(r => r.Id)];
    }

    private async Task<RecordingJob> RunAsync(PostEventReportSettings? settings = null, params IReportSection[] sections)
    {
        var job = CreateJob(settings, sections);
        await job.RunAsync(CancellationToken.None);
        return job;
    }

    #region Sending

    [TestMethod]
    public async Task FinishedEventWithViewers_WritesOneReportAndEmailsEveryAdmin()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com", "b@example.com");
        SeedViewing();

        var job = await RunAsync();

        var report = Reports().Single();
        Assert.AreEqual(PostEventReportState.Sent, report.State);
        Assert.AreEqual(2, report.RecipientCount);
        Assert.HasCount(2, job.Sent);
        Assert.Contains("Autumn Classic", job.Sent[0].Subject);
    }

    /// <summary>
    /// The admin copy rides on the first send only. A six-person organization would otherwise put six
    /// identical copies in the admin inbox every night of a race weekend.
    /// </summary>
    [TestMethod]
    public async Task AdminIsBlindCopiedOnExactlyOneOfTheSends()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com", "b@example.com", "c@example.com");
        SeedViewing();

        var job = await RunAsync();

        Assert.AreEqual(1, job.Sent.Count(s => s.Bcc != null));
    }

    [TestMethod]
    public async Task SecondRunOverTheSameData_SendsNothingAndWritesNoSecondReport()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com");
        SeedViewing();
        await RunAsync();

        var job = await RunAsync();

        Assert.IsEmpty(job.Sent);
        Assert.HasCount(1, Reports());
    }

    /// <summary>
    /// A send that fails for one recipient must leave the others sent, and must not be retried: the
    /// report row is the record that this event has been dealt with.
    /// </summary>
    [TestMethod]
    public async Task OneRecipientFailing_LeavesTheRestSentAndRecordsAPartialSend()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com", "bad@example.com", "c@example.com");
        SeedViewing();

        var job = CreateJob();
        job.FailFor.Add("bad@example.com");
        await job.RunAsync(CancellationToken.None);

        var report = Reports().Single();
        Assert.AreEqual(PostEventReportState.PartiallySent, report.State);
        Assert.AreEqual(2, report.RecipientCount);
        Assert.AreEqual(1, report.SendFailureCount);
    }

    [TestMethod]
    public async Task EveryRecipientFailing_RecordsAFailedReport()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com");
        SeedViewing();

        var job = CreateJob();
        job.FailFor.Add("a@example.com");
        await job.RunAsync(CancellationToken.None);

        Assert.AreEqual(PostEventReportState.Failed, Reports().Single().State);
    }

    #endregion

    #region Recipients

    /// <summary>
    /// The mapping's username is expected to be an email address but nothing enforces it, and the
    /// mail library throws on one that is not. Without filtering, a single badly provisioned volunteer
    /// account takes down the whole organization's report rather than its own copy.
    /// </summary>
    [TestMethod]
    public async Task AMappingThatIsNotAnEmailAddress_IsSkippedAndTheOthersStillReceive()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com", "not-an-email", "c@example.com");
        SeedViewing();

        var job = await RunAsync();

        Assert.HasCount(2, job.Sent);
        Assert.AreEqual(PostEventReportState.Sent, Reports().Single().State);
    }

    [TestMethod]
    public async Task TheSameAddressInDifferentCases_ReceivesOnlyOneCopy()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("Brian@Example.com", "brian@example.com");
        SeedViewing();

        var job = await RunAsync();

        Assert.HasCount(1, job.Sent);
    }

    [TestMethod]
    public async Task RecipientsAreCappedAndTheCapIsRecorded()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins([.. Enumerable.Range(0, 8).Select(i => $"a{i}@example.com")]);
        SeedViewing();

        var job = await RunAsync(new PostEventReportSettings { MaxRecipientsPerEvent = 3 });

        Assert.HasCount(3, job.Sent);
    }

    /// <summary>
    /// The numbers are worth keeping even when nobody is listed to receive them, and the state has to
    /// say which of the two happened: a missing administrator mapping needs doing something about, a
    /// quiet event does not.
    /// </summary>
    [TestMethod]
    public async Task NoAdmins_StillWritesTheReportAndRecordsThatThereWasNobodyToSendItTo()
    {
        SeedOrganization();
        SeedEvent();
        SeedViewing();

        var job = await RunAsync();

        Assert.IsEmpty(job.Sent);
        Assert.AreEqual(PostEventReportState.NoRecipients, Reports().Single().State);
    }

    /// <summary>
    /// Every mapping being unusable is the same situation as having none, and must not be confused
    /// with the event having had no viewers.
    /// </summary>
    [TestMethod]
    public async Task EveryMappingUnusable_IsRecordedAsHavingNoRecipients()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("service-account-relay", "another-machine-name");
        SeedViewing();

        var job = await RunAsync();

        Assert.IsEmpty(job.Sent);
        Assert.AreEqual(PostEventReportState.NoRecipients, Reports().Single().State);
    }

    [TestMethod]
    public async Task OrganizationThatOptedOut_IsSuppressed()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com");
        SeedViewing();
        using (var db = dbFactory.CreateDbContext())
        {
            db.OrganizationReportSettings.Add(new OrganizationReportSettings
            {
                OrganizationId = OrganizationId,
                SendPostEventReport = false,
            });
            db.SaveChanges();
        }

        var job = await RunAsync();

        Assert.IsEmpty(job.Sent);
        Assert.AreEqual(PostEventReportState.Suppressed, Reports().Single().State);
    }

    /// <summary>
    /// The first production run should go out this way: real events, real numbers, real rendering,
    /// and nothing reaching an organizer until the output has been read in a mail client.
    /// </summary>
    [TestMethod]
    public async Task DryRun_RedirectsTheSendAndNamesTheIntendedRecipient()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com");
        SeedViewing();

        var job = await RunAsync(new PostEventReportSettings { DryRunRecipient = "admin@redmist.racing" });

        var sent = job.Sent.Single();
        Assert.AreEqual("admin@redmist.racing", sent.To);
        Assert.Contains("a@example.com", sent.Subject);
    }

    /// <summary>
    /// A rehearsal has to leave the event reportable, or the rehearsal itself is what stops the
    /// organizer ever getting the report.
    /// </summary>
    /// <remarks>
    /// The candidate query excludes any event that has a report row at all, whatever its state. A dry
    /// run that wrote one would mark every event in the fortnight lookback as done - and the deploy
    /// instructions name a dry run as the step before going live, so this fires on the first
    /// production use of the feature or not at all.
    /// </remarks>
    [TestMethod]
    public async Task DryRun_RecordsNothingAndLeavesTheEventStillReportable()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com");
        SeedViewing();

        await RunAsync(new PostEventReportSettings { DryRunRecipient = "admin@redmist.racing" });
        Assert.IsEmpty(Reports(), "The dry run consumed the event.");

        // The real run afterwards still finds it.
        var real = await RunAsync();

        Assert.HasCount(1, real.Sent);
        Assert.AreEqual("a@example.com", real.Sent.Single().To);
        Assert.AreEqual(PostEventReportState.Sent, Reports().Single().State);
    }

    /// <summary>
    /// One event failing to save must not carry its rejected rows into every event after it. A shared
    /// context would, because Entity Framework leaves them on the change tracker.
    /// </summary>
    [TestMethod]
    public async Task OneEventFailingToSave_DoesNotStopTheEventsAfterIt()
    {
        SeedOrganization();
        SeedEvent(EventId, e => e.EndDate = Now.AddDays(-3));
        SeedEvent(EventId + 1, e => e.EndDate = Now.AddDays(-2));
        SeedViewing(EventId);
        SeedViewing(EventId + 1);
        SeedAdmins("a@example.com");

        var settings = new PostEventReportSettings();
        await RunAsync(settings, new ThrowsOnBuildForEvent(EventId), new ViewershipSection(settings));

        // The first event's section threw and was contained; both events still produced a report.
        Assert.HasCount(2, Reports());
    }

    #endregion

    #region Candidate selection

    [TestMethod]
    public async Task EventInsideTheSettleWindow_IsNotPickedUp()
    {
        SeedOrganization();
        SeedEvent(configure: e => e.EndDate = Now.AddHours(-2));
        SeedAdmins("a@example.com");
        SeedViewing();

        var job = await RunAsync();

        Assert.IsEmpty(job.Sent);
        Assert.IsEmpty(Reports());
    }

    [TestMethod]
    public async Task EventOlderThanTheLookback_IsNotPickedUp()
    {
        SeedOrganization();
        SeedEvent(configure: e => e.EndDate = Now.AddDays(-30));
        SeedAdmins("a@example.com");
        SeedViewing();

        await RunAsync();

        Assert.IsEmpty(Reports());
    }

    /// <summary>
    /// For a simulation this is the only thing standing between a load test and an organizer's
    /// inbox. Capture deliberately records simulation events like any other - it is how the whole
    /// path gets exercised, and a capture-side rule disagreed with the reconciler and cost the
    /// accurate data - so the flag is honored here and nowhere else. Note the seeded viewing: the
    /// point is that a simulation with real viewership behind it still produces no report.
    /// </summary>
    [TestMethod]
    [DataRow("deleted")]
    [DataRow("simulation")]
    [DataRow("live")]
    public async Task ExcludedEvent_IsNotReportedOn(string kind)
    {
        SeedOrganization();
        SeedEvent(configure: e =>
        {
            e.IsDeleted = kind == "deleted";
            e.IsSimulation = kind == "simulation";
            e.IsLive = kind == "live";
        });
        SeedAdmins("a@example.com");
        SeedViewing();

        await RunAsync();

        Assert.IsEmpty(Reports());
    }

    /// <summary>
    /// The social compose job excludes these because it publishes to Facebook. Nothing here is
    /// published - the report goes to the event's own organization - and a private event's numbers
    /// matter to its organizer more rather than less, because they are the only signal saying whether
    /// the access code circulated as intended. Asserted explicitly so the divergence is not later
    /// read as a copy-paste omission and "fixed".
    /// </summary>
    [TestMethod]
    [DataRow(true, false, DisplayName = "private")]
    [DataRow(false, true, DisplayName = "name hidden")]
    public async Task PrivateAndNameHiddenEvents_AreReportedOn(bool isPrivate, bool hideName)
    {
        SeedOrganization();
        SeedEvent(configure: e =>
        {
            e.IsPrivate = isPrivate;
            e.HideName = hideName;
        });
        SeedAdmins("a@example.com");
        SeedViewing();

        var job = await RunAsync();

        Assert.HasCount(1, job.Sent);
    }

    [TestMethod]
    public async Task MoreCandidatesThanTheRunBudget_AreLeftForTheNextRun()
    {
        SeedOrganization();
        for (var i = 0; i < 5; i++)
        {
            SeedEvent(EventId + i, e => e.EndDate = Now.AddDays(-2).AddHours(-i));
            SeedViewing(EventId + i);
        }
        SeedAdmins("a@example.com");

        await RunAsync(new PostEventReportSettings { MaxEventsPerRun = 2 });

        Assert.HasCount(2, Reports());
    }

    #endregion

    #region The last day

    /// <summary>
    /// The end date is stored as midnight at the start of the race day, and the report's upper bound
    /// used to be that midnight plus twelve hours - 08:00 on the east coast, before anybody had
    /// arrived. A single-day event in the Americas then found nobody watching at all, and its
    /// organizer got no report.
    /// </summary>
    [TestMethod]
    public async Task ASingleDayEventInTheAmericas_IsReportedWithItsRaceDay()
    {
        var raceDay = new DateTime(2026, 9, 17);
        SeedOrganization();
        SeedEvent(configure: e => { e.StartDate = raceDay; e.EndDate = raceDay; });
        SeedAdmins("a@example.com");
        SeedViewer(raceDay.AddHours(14), raceDay.AddHours(23.5));
        SeedViewer(raceDay.AddHours(15), raceDay.AddHours(18));
        SeedViewer(raceDay.AddHours(20), raceDay.AddHours(23.5));

        var settings = new PostEventReportSettings();
        var job = await RunAsync(settings);

        Assert.AreEqual(PostEventReportState.Sent, Reports().Single().State);
        Assert.HasCount(1, job.Sent);
        var viewership = Viewership()!;
        Assert.AreEqual(570 + 180 + 210, viewership.TotalViewerMinutes, 0.1);
        Assert.IsTrue(viewership.TotalViewerMinutes >= settings.MinViewerMinutes);
    }

    /// <summary>
    /// The last day of a longer event, likewise: racing after noon UTC is counted, and a row still
    /// open from the final afternoon is counted to the bound. Under the old bound its clamped end fell
    /// before its start, and it was thrown away as anomalous.
    /// </summary>
    [TestMethod]
    public async Task AMultiDayEvent_CountsItsLastAfternoon_AndClampsAnOpenLastDayRow()
    {
        var firstDay = new DateTime(2026, 9, 15);
        var lastDay = new DateTime(2026, 9, 17);
        SeedOrganization();
        SeedEvent(configure: e => { e.StartDate = firstDay; e.EndDate = lastDay; });
        SeedAdmins("a@example.com");
        SeedViewer(firstDay.AddHours(15), firstDay.AddHours(16));
        SeedViewer(lastDay.AddHours(13), lastDay.AddHours(20));
        SeedViewer(lastDay.AddHours(18), null);

        await RunAsync();

        var viewership = Viewership()!;
        Assert.AreEqual(0, viewership.AnomalousSessions, "The open last-day row was discarded as anomalous.");
        Assert.AreEqual(1, viewership.OpenSessions);
        // 60 on the first day, 420 on the last afternoon, and the open row from 18:00 to the bound at
        // 12:00 the next day.
        Assert.AreEqual(60 + 420 + 18 * 60, viewership.TotalViewerMinutes, 0.1);
        Assert.AreEqual(lastDay.AddHours(36), viewership.WindowEndUtc);
    }

    /// <summary>
    /// The plausible span is now twelve hours, every day of the event, and twelve hours: for a
    /// Friday-to-Sunday event exactly the 96-hour maximum window, so nothing is truncated even when a
    /// viewer spans the whole of it.
    /// </summary>
    [TestMethod]
    public async Task AFridayToSundayEvent_FitsTheLongestWindow_WithoutTruncation()
    {
        var friday = new DateTime(2026, 9, 15);
        var sunday = new DateTime(2026, 9, 17);
        SeedOrganization();
        SeedEvent(configure: e => { e.StartDate = friday; e.EndDate = sunday; });
        SeedAdmins("a@example.com");
        SeedViewer(friday.AddHours(-16), null);

        var settings = new PostEventReportSettings();
        await RunAsync(settings);

        var viewership = Viewership()!;
        Assert.AreEqual(friday.AddHours(-12), viewership.WindowStartUtc);
        Assert.AreEqual(sunday.AddHours(36), viewership.WindowEndUtc);
        Assert.AreEqual(settings.MaxWindow, viewership.WindowEndUtc - viewership.WindowStartUtc);
        Assert.AreEqual(settings.MaxWindow.TotalMinutes, viewership.TotalViewerMinutes, 0.1);
    }

    /// <summary>
    /// A Thursday-to-Sunday event's plausible span is 120 hours, past the 96-hour maximum. One
    /// connection on the Wednesday night pins the window start, and truncating at the maximum would
    /// then cut the event off on Sunday afternoon Pacific - the report short of what the live view
    /// showed. The span already bounds the window, so nothing inside it is truncated.
    /// </summary>
    [TestMethod]
    public async Task AFourDayEvent_WithAConnectionTheNightBefore_IsNotTruncated_AndAgreesWithTheLiveView()
    {
        var thursday = new DateTime(2026, 9, 14);
        var sunday = new DateTime(2026, 9, 17);
        SeedOrganization();
        SeedEvent(configure: e => { e.StartDate = thursday; e.EndDate = sunday; });
        SeedAdmins("a@example.com");
        SeedViewer(thursday.AddHours(-2), thursday.AddHours(-1));
        SeedViewer(sunday.AddHours(20), sunday.AddHours(25));
        SeedViewer(sunday.AddHours(21), null);

        var settings = new PostEventReportSettings();
        await RunAsync(settings);

        var report = Viewership()!;
        LiveViewershipDto live;
        using (var db = dbFactory.CreateDbContext())
        {
            live = LiveViewershipCalculator.Compute(EventId, thursday, sunday, eventIsLive: false,
                [.. db.Sessions.AsNoTracking().Where(x => x.EventId == EventId)],
                [.. db.EventViewerSessions.AsNoTracking().Where(x => x.EventId == EventId)], Now);
        }

        Assert.IsTrue(report.WindowEndUtc - report.WindowStartUtc > settings.MaxWindow,
            "The fixture no longer spans more than the maximum window, so it proves nothing.");
        Assert.AreEqual(sunday.AddHours(36), report.WindowEndUtc, "The window was truncated inside the plausible span.");
        // An hour on the Wednesday night, five on Sunday afternoon, and the open row from 21:00 to
        // the bound at 12:00 the next day.
        Assert.AreEqual(60 + 300 + 15 * 60, report.TotalViewerMinutes, 0.1);
        Assert.AreEqual(report.WindowStartUtc, live.Event.WindowStartUtc);
        Assert.AreEqual(report.TotalViewerMinutes, live.Event.TotalViewerSeconds / 60d, 0.1,
            "The report and the live view counted different time.");
        Assert.AreEqual(report.MaxConcurrent, live.Event.MaxConcurrent);
    }

    /// <summary>
    /// The report the job actually produces - its own candidate query, session windows and bounds,
    /// nothing chosen by the test - against the live view of the same rows read after the event. They
    /// have to tell the organizer the same thing about the final day, which is exactly where the old
    /// bound had the report stop at noon UTC while the live view carried on.
    /// </summary>
    [TestMethod]
    public async Task TheReport_AgreesWithTheLiveView_AboutAFinishedEventsLastDay()
    {
        var saturday = new DateTime(2026, 9, 16);
        var sunday = new DateTime(2026, 9, 17);
        SeedOrganization();
        SeedEvent(configure: e => { e.StartDate = saturday; e.EndDate = sunday; });
        SeedAdmins("a@example.com");
        using (var db = dbFactory.CreateDbContext())
        {
            db.Sessions.AddRange(
                new Session { Id = 1, EventId = EventId, Name = "Saturday race", StartTime = saturday.AddHours(14),
                    EndTime = saturday.AddHours(18), LocalTimeZoneOffset = -4 },
                new Session { Id = 2, EventId = EventId, Name = "Sunday race", StartTime = sunday.AddHours(13),
                    EndTime = sunday.AddHours(17), LocalTimeZoneOffset = -4 });
            db.SaveChanges();
        }
        SeedViewer(saturday.AddHours(15), saturday.AddHours(17));
        SeedViewer(sunday.AddHours(13.5), sunday.AddHours(16.75));
        SeedViewer(sunday.AddHours(14), sunday.AddHours(15));
        SeedViewer(sunday.AddHours(19), null);
        SeedViewer(sunday.AddHours(16), sunday.AddHours(15));

        await RunAsync();

        LiveViewershipDto live;
        EventViewershipSummary report;
        using (var db = dbFactory.CreateDbContext())
        {
            live = LiveViewershipCalculator.Compute(EventId, saturday, sunday, eventIsLive: false,
                [.. db.Sessions.AsNoTracking().Where(x => x.EventId == EventId)],
                [.. db.EventViewerSessions.AsNoTracking().Where(x => x.EventId == EventId)], Now);
            report = db.PostEventReports.Include(r => r.Viewership!).ThenInclude(v => v.Sessions)
                .Single(r => r.EventId == EventId).Viewership!;
        }

        Assert.AreEqual(report.WindowStartUtc, live.Event.WindowStartUtc);
        Assert.AreEqual(report.MaxConcurrent, live.Event.MaxConcurrent);
        Assert.AreEqual(report.TotalViewerMinutes, live.Event.TotalViewerSeconds / 60d, 0.1,
            "The report and the live view counted different time.");
        Assert.AreEqual(report.TrackOffsetMinutes, live.TrackOffsetMinutes);
        Assert.HasCount(2, live.Sessions);
        foreach (var session in live.Sessions)
        {
            var reported = report.Sessions.Single(x => x.SessionId == session.SessionId);
            Assert.AreEqual(reported.StartUtc, session.StartUtc);
            Assert.AreEqual(reported.EndUtc, session.EndUtc);
            Assert.AreEqual(reported.MaxConcurrent, session.MaxConcurrent, $"Session {session.SessionId} peak.");
            Assert.AreEqual(reported.TotalViewerMinutes, session.TotalViewerSeconds / 60d, 0.1,
                $"Session {session.SessionId} total.");
        }
    }

    /// <summary>
    /// Measured from the end date's midnight, a settle period shorter than a day made an event due
    /// while its last day was still being raced, with only the live flag holding it back. That flag
    /// clears once the relay has been silent for ten minutes.
    /// </summary>
    [TestMethod]
    public async Task AnEventIsNotDueDuringItsLastDay_EvenWithAShortSettlePeriod()
    {
        var lastDay = Now.Date;
        SeedOrganization();
        SeedEvent(configure: e => { e.StartDate = lastDay.AddDays(-1); e.EndDate = lastDay; });
        var settings = new PostEventReportSettings { SettlePeriod = TimeSpan.FromHours(6) };

        clock.SetUtcNow(new DateTimeOffset(lastDay.AddHours(20), TimeSpan.Zero));
        Assert.IsEmpty(await CandidatesAsync(settings), "Due at 20:00 on its own last day.");

        clock.SetUtcNow(new DateTimeOffset(lastDay.AddDays(1).AddHours(6).AddTicks(-1), TimeSpan.Zero));
        Assert.IsEmpty(await CandidatesAsync(settings));

        clock.SetUtcNow(new DateTimeOffset(lastDay.AddDays(1).AddHours(6), TimeSpan.Zero));
        CollectionAssert.AreEqual(new[] { EventId }, await CandidatesAsync(settings));
    }

    [TestMethod]
    public async Task AnEventIsDue_ASettlePeriodAfterItsLastDayEnds()
    {
        var lastDay = Now.Date.AddDays(-1);
        SeedOrganization();
        SeedEvent(configure: e => { e.StartDate = lastDay.AddDays(-1); e.EndDate = lastDay; });

        Assert.IsEmpty(await CandidatesAsync(), "Due a settle period after the end date's midnight.");

        clock.SetUtcNow(new DateTimeOffset(lastDay.AddDays(2).AddTicks(-1), TimeSpan.Zero));
        Assert.IsEmpty(await CandidatesAsync());

        clock.SetUtcNow(new DateTimeOffset(lastDay.AddDays(2), TimeSpan.Zero));
        CollectionAssert.AreEqual(new[] { EventId }, await CandidatesAsync());
    }

    /// <summary>The lookback is measured from the same moment as the settle period.</summary>
    [TestMethod]
    public async Task AnEventStaysEligible_ForTheLookbackAfterItsLastDayEnds()
    {
        // The last day ended at 00:00 on the 6th; fourteen days on is 00:00 on the 20th.
        var lastDay = new DateTime(2026, 9, 5);
        SeedOrganization();
        SeedEvent(configure: e => { e.StartDate = lastDay; e.EndDate = lastDay; });

        CollectionAssert.AreEqual(new[] { EventId }, await CandidatesAsync(),
            "Dropped fourteen days after the end date's midnight rather than after the last day.");

        clock.SetUtcNow(new DateTimeOffset(lastDay.AddDays(15), TimeSpan.Zero));
        Assert.IsEmpty(await CandidatesAsync());
    }

    /// <summary>
    /// The dashboard's report status and the job have to agree hour by hour about every event: it is
    /// listed only once its last day has ended, and the job picks it up exactly when it was listed a
    /// settle period ago and is still eligible. A dashboard saying "pending" for an event the job will
    /// not touch, or "no report yet" for one still being raced, would be telling the organizer
    /// something untrue.
    /// </summary>
    /// <remarks>
    /// Two status readers, one lagging the other by the settle period, because "listed a settle period
    /// ago" cannot be asked of a clock that only moves forward. Swept hour by hour from a midnight,
    /// so every boundary - all of them fall on midnights - is sampled exactly.
    /// </remarks>
    [TestMethod]
    public async Task ReportStatus_AgreesWithTheJob_AboutWhenEachEventIsPickedUp()
    {
        var settings = new PostEventReportSettings();
        var start = Now.Date.AddDays(1);
        SeedOrganization();
        var endDates = Enumerable.Range(0, 17).Select(k => start.AddDays(-k))
            .Append(start.AddDays(-2).AddHours(15))
            .Append(start.AddDays(-14).AddHours(23))
            .ToList();
        for (var i = 0; i < endDates.Count; i++)
        {
            var endDate = endDates[i];
            SeedEvent(EventId + i, e => { e.StartDate = endDate.AddDays(-1); e.EndDate = endDate; });
        }

        clock.SetUtcNow(new DateTimeOffset(start, TimeSpan.Zero));
        var lagging = new FakeTimeProvider(new DateTimeOffset(start - settings.SettlePeriod));
        var statusNow = StatusReader(clock);
        var statusThen = StatusReader(lagging);

        for (var hour = 0; hour <= 17 * 24; hour++)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var picked = (await CandidatesAsync(settings)).ToHashSet();
            var listed = (await statusNow.ReportStatus(OrganizationId, take: 100)).Value!.ToDictionary(s => s.EventId);
            var listedThen = (await statusThen.ReportStatus(OrganizationId, take: 100)).Value!
                .Select(s => s.EventId).ToHashSet();

            for (var i = 0; i < endDates.Count; i++)
            {
                var id = EventId + i;
                var lastDayEnded = endDates[i].Date.AddDays(1) <= now;
                Assert.AreEqual(lastDayEnded, listed.ContainsKey(id),
                    $"At {now:MM-dd HH:mm}, event ending {endDates[i]:MM-dd HH:mm} was listed={listed.ContainsKey(id)}.");

                var expected = listedThen.Contains(id) && listed.TryGetValue(id, out var status) && status.Eligible;
                Assert.AreEqual(expected, picked.Contains(id),
                    $"At {now:MM-dd HH:mm}, event ending {endDates[i]:MM-dd HH:mm}: the job and the dashboard disagree.");
            }

            clock.Advance(TimeSpan.FromHours(1));
            lagging.Advance(TimeSpan.FromHours(1));
        }
    }

    private ViewershipController StatusReader(TimeProvider time)
    {
        var controller = new ViewershipController(new DebugLoggerFactory(), dbFactory,
            new ConfigurationBuilder().Build(), new FakeHybridCache(), time);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", "relay-test")], "Test")),
            },
        };
        return controller;
    }

    #endregion

    #region Content

    /// <summary>
    /// An email saying nobody watched is the most demoralising thing this feature could do, and in
    /// practice would usually mean our own capture broke rather than that nobody cared.
    /// </summary>
    [TestMethod]
    public async Task EventWithNoViewers_IsRecordedAsNoContentAndNothingIsSent()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com");

        var job = await RunAsync();

        Assert.IsEmpty(job.Sent);
        Assert.AreEqual(PostEventReportState.NoContent, Reports().Single().State);
    }

    [TestMethod]
    public async Task TheSectionsThatProducedContent_AreRecorded()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com");
        SeedViewing();

        await RunAsync();

        Assert.Contains(ViewershipSection.SectionCode, Reports().Single().SectionsJson);
    }

    /// <summary>
    /// A section that throws costs its own block and nothing else. That is the whole point of the
    /// seam, and the case that decides whether adding a section is safe.
    /// </summary>
    [TestMethod]
    public async Task ASectionThatThrows_DoesNotStopTheRestOfTheReport()
    {
        SeedOrganization();
        SeedEvent();
        SeedAdmins("a@example.com");
        SeedViewing();

        var settings = new PostEventReportSettings();
        var job = await RunAsync(settings, new ThrowingSection(), new ViewershipSection(settings));

        Assert.AreEqual(PostEventReportState.Sent, Reports().Single().State);
        Assert.IsTrue(job.Sent.Any(s => s.To == "a@example.com"));
    }

    [TestMethod]
    public async Task ASectionFailingForOneEvent_DoesNotAbortTheRun()
    {
        SeedOrganization();
        SeedEvent(EventId, e => e.EndDate = Now.AddDays(-3));
        SeedEvent(EventId + 1, e => e.EndDate = Now.AddDays(-2));
        SeedViewing(EventId);
        SeedViewing(EventId + 1);
        SeedAdmins("a@example.com");

        var settings = new PostEventReportSettings();
        await RunAsync(settings, new ThrowsOnBuildForEvent(EventId), new ViewershipSection(settings));

        // The first event's section threw and was omitted; the viewership section still had content,
        // so both events produced a report.
        Assert.HasCount(2, Reports());
    }

    [TestMethod]
    public async Task TheHostIsStoppedWhenTheRunFinishes()
    {
        SeedOrganization();

        await RunAsync();

        lifetime.Verify(l => l.StopApplication(), Times.Once);
    }

    private sealed class ThrowingSection : IReportSection
    {
        public string Code => "throwing";
        public int Order => 1;
        public Task<ReportSectionResult?> BuildAsync(ReportContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("section is broken");
    }

    private sealed class ThrowsOnBuildForEvent(int eventId) : IReportSection
    {
        public string Code => "conditional";
        public int Order => 1;
        public Task<ReportSectionResult?> BuildAsync(ReportContext context, CancellationToken cancellationToken)
            => context.Event.Id == eventId
                ? throw new InvalidOperationException("section is broken for this event")
                : Task.FromResult<ReportSectionResult?>(null);
    }

    #endregion
}
