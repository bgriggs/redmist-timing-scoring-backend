using BigMission.TestHelpers.Testing;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.PostEventReports;
using RedMist.PostEventReports.Sections;
using RedMist.PostEventReports.Sections.Viewership;
using RedMist.PostEventReports.Suggestions;
using RedMist.TimingCommon.Models;
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
