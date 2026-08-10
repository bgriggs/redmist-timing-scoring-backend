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
using RedMist.SponsorReports;

namespace RedMist.TimingAndScoringService.Tests.SponsorReports;

/// <summary>
/// Tests for the monthly sponsor report mailer. The job picks up unprocessed
/// <see cref="SponsorStatistics"/> rows, renders an HTML report and marks each row processed.
/// </summary>
/// <remarks>
/// <see cref="EmailHelper"/> is a concrete SMTP client in RedMist.Backend.Shared with no interface
/// and a non-virtual <c>SendEmailAsync</c>, so it cannot be mocked. The job exposes a
/// <c>protected virtual SendEmailAsync</c> seam instead, which <see cref="RecordingReportJob"/>
/// overrides to capture what would have been sent. As a second line of defense the helper handed to
/// the job builds a mocked <c>ISmtpClient</c>, so nothing here can open a connection even if a test
/// takes a path that bypasses the job's seam.
/// </remarks>
[TestClass]
public class SponsorReportJobTests
{
    private const string AdminEmail = "brian@bigmissionmotorsports.com";
    private const string FromEmail = "Red Mist <support@redmist.racing>";

    private static readonly DateTimeOffset RunTime = new(2026, 3, 5, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly February2026 = new(2026, 2, 1);

    private readonly DebugLoggerFactory lf = new();

    private sealed record SentEmail(string Subject, string BodyHtml, string To, string From, string? Bcc);

    /// <summary>
    /// Test double that records every outbound email instead of talking to SMTP, and can be
    /// told to fail for a given recipient to exercise the per-sponsor error path.
    /// </summary>
    private sealed class RecordingReportJob(
        ILoggerFactory loggerFactory,
        IDbContextFactory<TsContext> contextFactory,
        EmailHelper emailHelper,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider)
        : SponsorReportJob(loggerFactory, contextFactory, emailHelper, lifetime, timeProvider)
    {
        public List<SentEmail> Sent { get; } = [];

        /// <summary>Recipients for which the send must throw, simulating an SMTP failure.</summary>
        public HashSet<string> FailFor { get; } = [];

        protected override Task SendEmailAsync(string subject, string bodyHtml, string to, string from, string? bcc)
        {
            if (FailFor.Contains(to))
            {
                // Recorded before throwing so the test can see the attempt.
                Sent.Add(new SentEmail(subject, bodyHtml, to, from, bcc));
                throw new InvalidOperationException($"SMTP refused {to}");
            }

            Sent.Add(new SentEmail(subject, bodyHtml, to, from, bcc));
            return Task.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public required DbContextOptions<TsContext> Options { get; init; }
        public required IDbContextFactory<TsContext> Factory { get; init; }
        public required Mock<IHostApplicationLifetime> Lifetime { get; init; }
        public required CancellationTokenSource AppStarted { get; init; }
        public required FakeTimeProvider Clock { get; init; }

        public TsContext NewContext() => new(Options);
    }

    private static Harness CreateHarness(DateTimeOffset? now = null, bool applicationAlreadyStarted = true)
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var appStarted = new CancellationTokenSource();
        if (applicationAlreadyStarted)
        {
            appStarted.Cancel();
        }

        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(l => l.ApplicationStarted).Returns(appStarted.Token);

        return new Harness
        {
            Options = options,
            Factory = new TestDbContextFactory(options),
            Lifetime = lifetime,
            AppStarted = appStarted,
            Clock = new FakeTimeProvider(now ?? RunTime),
        };
    }

    /// <summary>
    /// An <see cref="EmailHelper"/> whose SMTP client is a mock, so a send cannot open a socket.
    /// </summary>
    /// <remarks>
    /// <see cref="RecordingReportJob"/> overrides the job's own send seam, but that is the only thing
    /// standing between these tests and MailKit's ConnectAsync. Overriding CreateSmtpClient as well
    /// means no arrangement of the job under test can reach the network, whichever path it takes.
    /// This is the same seam <c>EmailHelperTests.TestableEmailHelper</c> uses.
    /// </remarks>
    private sealed class UnconnectedEmailHelper(IConfiguration configuration) : EmailHelper(configuration)
    {
        protected override ISmtpClient CreateSmtpClient() => Mock.Of<ISmtpClient>();
    }

    private static EmailHelper CreateEmailHelper()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Host"] = "smtp.invalid",
                ["Email:Port"] = "587",
                ["Email:Username"] = "user",
                ["Email:Password"] = "pass",
            })
            .Build();
        return new UnconnectedEmailHelper(config);
    }

    private RecordingReportJob CreateJob(Harness h, TimeProvider? clock = null)
        => new(lf, h.Factory, CreateEmailHelper(), h.Lifetime.Object, clock ?? h.Clock);

    private static Sponsor NewSponsor(int id, string name = "Acme", string contactEmail = "contact@acme.test",
        bool sendMonthlyReport = true, string contactName = "Ada") => new()
        {
            Id = id,
            Name = name,
            ImageUrl = $"https://cdn.test/{id}.png",
            TargetUrl = "https://acme.test",
            ContactName = contactName,
            ContactEmail = contactEmail,
            SendMonthlyReport = sendMonthlyReport,
        };

    private static SponsorStatistics NewStats(int sponsorId, bool processed = false, DateOnly? month = null) => new()
    {
        SponsorId = sponsorId,
        Month = month ?? February2026,
        Impressions = 1234,
        ViewableImpressions = 567,
        ClickThroughs = 89,
        EngagementDurationMs = 65_000,
        ReportProcessed = processed,
    };

    private static RedMist.TimingCommon.Models.Configuration.Event NewEvent(int id, string name, bool isSimulation = false) => new()
    {
        Id = id,
        OrganizationId = 1,
        Name = name,
        IsSimulation = isSimulation,
    };

    #region Selection and idempotency

    /// <summary>With nothing unprocessed the job sends no mail at all and stops the host.</summary>
    [TestMethod]
    public async Task Report_NoUnprocessedStatistics_SendsNothingAndStopsHost()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(1));
            seed.SponsorStatistics.Add(NewStats(1, processed: true));
            await seed.SaveChangesAsync();
        }

        var job = CreateJob(h);
        await job.RunReportsAsync(CancellationToken.None);

        Assert.AreEqual(0, job.Sent.Count);
        h.Lifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);
    }

    /// <summary>
    /// Only rows with ReportProcessed == false are mailed, and a second pass over the same
    /// database sends nothing more - the flag is the re-send guard.
    /// </summary>
    [TestMethod]
    public async Task Report_ProcessesOnlyUnprocessed_AndSecondRunResendsNothing()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.AddRange(NewSponsor(1, "One", "one@acme.test"), NewSponsor(2, "Two", "two@acme.test"));
            var alreadyDone = NewStats(2, processed: true);
            alreadyDone.ReportProcessingSuccessful = true;
            seed.SponsorStatistics.AddRange(NewStats(1, processed: false), alreadyDone);
            await seed.SaveChangesAsync();
        }

        var first = CreateJob(h);
        await first.RunReportsAsync(CancellationToken.None);

        Assert.AreEqual(1, first.Sent.Count);
        Assert.AreEqual("one@acme.test", first.Sent[0].To);

        await using (var db = h.NewContext())
        {
            var one = await db.SponsorStatistics.SingleAsync(s => s.SponsorId == 1);
            Assert.IsTrue(one.ReportProcessed);
            Assert.IsTrue(one.ReportProcessingSuccessful);
            var two = await db.SponsorStatistics.SingleAsync(s => s.SponsorId == 2);
            Assert.IsTrue(two.ReportProcessingSuccessful, "An already-processed row must not be touched.");
        }

        var second = CreateJob(h);
        await second.RunReportsAsync(CancellationToken.None);
        Assert.AreEqual(0, second.Sent.Count, "Re-running must not re-send reports.");
    }

    #endregion

    #region Recipient routing

    /// <summary>A sponsor opted in with a contact address gets the report, admin is BCC'd.</summary>
    [TestMethod]
    public async Task Report_SponsorOptedIn_SendsToContactWithAdminBcc()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(1, "Acme", "contact@acme.test", sendMonthlyReport: true));
            seed.SponsorStatistics.Add(NewStats(1));
            await seed.SaveChangesAsync();
        }

        var job = CreateJob(h);
        await job.RunReportsAsync(CancellationToken.None);

        var sent = job.Sent.Single();
        Assert.AreEqual("contact@acme.test", sent.To);
        Assert.AreEqual(AdminEmail, sent.Bcc);
        Assert.AreEqual(FromEmail, sent.From);
        Assert.AreEqual("Red Mist Sponsor Report - February 2026", sent.Subject);
    }

    /// <summary>
    /// Opted out, or opted in with no usable address: the report still gets built but goes to
    /// the admin only, with no BCC.
    /// </summary>
    [TestMethod]
    [DataRow(false, "contact@acme.test", DisplayName = "Opted out")]
    [DataRow(true, "", DisplayName = "Opted in with no address")]
    [DataRow(true, "   ", DisplayName = "Opted in with whitespace address")]
    public async Task Report_NotDeliverableToSponsor_SendsToAdminOnly(bool sendMonthlyReport, string contactEmail)
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(1, "Acme", contactEmail, sendMonthlyReport));
            seed.SponsorStatistics.Add(NewStats(1));
            await seed.SaveChangesAsync();
        }

        var job = CreateJob(h);
        await job.RunReportsAsync(CancellationToken.None);

        var sent = job.Sent.Single();
        Assert.AreEqual(AdminEmail, sent.To);
        Assert.IsNull(sent.Bcc);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.SingleAsync();
        Assert.IsTrue(stats.ReportProcessed);
        Assert.IsTrue(stats.ReportProcessingSuccessful, "Delivering to admin still counts as a successful run.");
    }

    #endregion

    #region Failure handling

    /// <summary>
    /// Statistics referencing a sponsor that no longer exists are marked processed but
    /// unsuccessful, and an admin failure notification is raised rather than a report.
    /// </summary>
    [TestMethod]
    public async Task Report_SponsorMissing_MarksUnsuccessfulAndNotifiesAdmin()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            // No Sponsor row for id 42.
            seed.SponsorStatistics.Add(NewStats(42));
            await seed.SaveChangesAsync();
        }

        var job = CreateJob(h);
        await job.RunReportsAsync(CancellationToken.None);

        var sent = job.Sent.Single();
        Assert.AreEqual("Red Mist Sponsor Report - Processing Error", sent.Subject);
        Assert.AreEqual(AdminEmail, sent.To);
        StringAssert.Contains(sent.BodyHtml, "Sponsor ID 42 not found");
        StringAssert.Contains(sent.BodyHtml, "2026-02");

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.SingleAsync();
        Assert.IsTrue(stats.ReportProcessed, "The row must not be retried forever.");
        Assert.IsFalse(stats.ReportProcessingSuccessful);
    }

    /// <summary>
    /// A send failure for one sponsor is contained: that row is marked processed-unsuccessful
    /// with an admin notification, and the remaining sponsors are still reported on.
    /// </summary>
    [TestMethod]
    public async Task Report_SendFailureForOneSponsor_IsContainedAndOthersStillSent()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.AddRange(
                NewSponsor(1, "Broken", "broken@acme.test"),
                NewSponsor(2, "Working", "working@acme.test"));
            seed.SponsorStatistics.AddRange(NewStats(1), NewStats(2));
            await seed.SaveChangesAsync();
        }

        var job = CreateJob(h);
        job.FailFor.Add("broken@acme.test");
        await job.RunReportsAsync(CancellationToken.None);

        Assert.IsTrue(job.Sent.Any(e => e.To == "working@acme.test" && e.Subject.StartsWith("Red Mist Sponsor Report - February")),
            "The healthy sponsor must still receive a report.");
        var notification = job.Sent.Single(e => e.Subject == "Red Mist Sponsor Report - Processing Error");
        StringAssert.Contains(notification.BodyHtml, "&#39;Broken&#39;");
        StringAssert.Contains(notification.BodyHtml, "SMTP refused");

        await using var db = h.NewContext();
        var broken = await db.SponsorStatistics.SingleAsync(s => s.SponsorId == 1);
        Assert.IsTrue(broken.ReportProcessed);
        Assert.IsFalse(broken.ReportProcessingSuccessful);
        var working = await db.SponsorStatistics.SingleAsync(s => s.SponsorId == 2);
        Assert.IsTrue(working.ReportProcessingSuccessful);
    }

    #endregion

    #region Report content

    /// <summary>
    /// The event breakdown resolves ids to names, keeps the "no event" bucket, and drops
    /// events that are not in the database or that are simulations.
    /// </summary>
    [TestMethod]
    public async Task Report_EventBreakdown_ResolvesNamesAndDropsUnknownAndSimulationEvents()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(1));
            seed.Events.AddRange(NewEvent(10, "Thunderhill 25"), NewEvent(11, "Sim Race", isSimulation: true));
            var stats = NewStats(1);
            stats.EventStatistics =
            [
                new EventSponsorStatistics { SponsorId = 1, EventId = 10, Impressions = 100 },
                new EventSponsorStatistics { SponsorId = 1, EventId = 11, Impressions = 90 },
                new EventSponsorStatistics { SponsorId = 1, EventId = 12, Impressions = 80 },
                new EventSponsorStatistics { SponsorId = 1, EventId = 0, Impressions = 70 },
            ];
            seed.SponsorStatistics.Add(stats);
            await seed.SaveChangesAsync();
        }

        var job = CreateJob(h);
        await job.RunReportsAsync(CancellationToken.None);

        var html = job.Sent.Single().BodyHtml;
        StringAssert.Contains(html, "Thunderhill 25");
        StringAssert.Contains(html, "No Event");
        Assert.IsFalse(html.Contains("Sim Race"), "Simulation events must not appear in a sponsor report.");
        Assert.IsFalse(html.Contains("Unknown"), "An event id with no matching event row must be dropped, not shown as Unknown.");
        Assert.IsFalse(html.Contains("<td>80</td>"), "The dropped event's numbers must not be rendered either.");
    }

    /// <summary>Both breakdown tables are ordered by impressions, highest first.</summary>
    [TestMethod]
    public void BuildReportHtml_OrdersBreakdownsByImpressionsDescending()
    {
        var stats = NewStats(1);
        stats.EventStatistics =
        [
            new EventSponsorStatistics { SponsorId = 1, EventId = 10, Impressions = 5 },
            new EventSponsorStatistics { SponsorId = 1, EventId = 11, Impressions = 50 },
        ];
        stats.SourceStatistics =
        [
            new SourceSponsorStatistics { SponsorId = 1, Source = "mobile", Impressions = 3 },
            new SourceSponsorStatistics { SponsorId = 1, Source = "web", Impressions = 30 },
        ];
        var names = new Dictionary<int, string> { [10] = "Low Event", [11] = "High Event" };

        var html = SponsorReportJob.BuildReportHtml(stats, NewSponsor(1), "February 2026", names, new DateOnly(2026, 3, 5));

        Assert.IsTrue(html.IndexOf("High Event", StringComparison.Ordinal) < html.IndexOf("Low Event", StringComparison.Ordinal),
            "The busiest event must be listed first.");
        Assert.IsTrue(html.IndexOf("<td>web</td>", StringComparison.Ordinal) < html.IndexOf("<td>mobile</td>", StringComparison.Ordinal),
            "The busiest source must be listed first.");
    }

    /// <summary>
    /// When every per-event row references an unresolvable event, all of them are filtered out
    /// yet the section header and an empty table are still emitted - a cosmetic wart worth
    /// pinning - and the "Unknown" fallback name is never reachable because the filter runs first.
    /// </summary>
    [TestMethod]
    public void BuildReportHtml_AllEventIdsUnresolvable_EmitsEmptyBreakdownTable()
    {
        var stats = NewStats(1);
        stats.EventStatistics = [new EventSponsorStatistics { SponsorId = 1, EventId = 77, Impressions = 5 }];

        var html = SponsorReportJob.BuildReportHtml(stats, NewSponsor(1), "February 2026", new Dictionary<int, string>(), new DateOnly(2026, 3, 5));

        StringAssert.Contains(html, "Event Breakdown");
        Assert.IsFalse(html.Contains("Unknown"), "The filter removes unresolvable events before the name fallback can apply.");
        Assert.IsFalse(html.Contains("<td>5</td>"), "The unresolvable event's numbers must not be rendered.");
    }

    /// <summary>The greeting uses the contact name, falling back to the sponsor name.</summary>
    [TestMethod]
    [DataRow("Ada", "Hello Ada,")]
    [DataRow("", "Hello Acme,")]
    [DataRow("   ", "Hello Acme,")]
    public void BuildReportHtml_GreetsContactNameOrFallsBackToSponsorName(string contactName, string expected)
    {
        var sponsor = NewSponsor(1, "Acme", contactName: contactName);

        var html = SponsorReportJob.BuildReportHtml(NewStats(1), sponsor, "February 2026", new Dictionary<int, string>(), new DateOnly(2026, 3, 5));

        StringAssert.Contains(html, expected);
    }

    /// <summary>
    /// The subscription footer counts whole calendar months to the subscription end, clamps at
    /// zero once expired, and is omitted entirely when the subscription window is not set.
    /// </summary>
    [TestMethod]
    [DataRow(2026, 9, 30, "6 months remaining")]
    [DataRow(2026, 4, 1, "1 month remaining")]
    [DataRow(2027, 3, 1, "12 months remaining")]
    [DataRow(2026, 3, 15, "0 months remaining")]
    [DataRow(2025, 12, 1, "0 months remaining")]
    public void BuildReportHtml_SubscriptionFooter_CountsWholeMonthsAndClampsAtZero(int year, int month, int day, string expected)
    {
        var sponsor = NewSponsor(1);
        sponsor.SubscriptionStart = new DateOnly(2025, 3, 1);
        sponsor.SubscriptionEnd = new DateOnly(year, month, day);

        var html = SponsorReportJob.BuildReportHtml(NewStats(1), sponsor, "February 2026", new Dictionary<int, string>(), new DateOnly(2026, 3, 5));

        StringAssert.Contains(html, expected);
    }

    /// <summary>No subscription dates means no subscription paragraph.</summary>
    [TestMethod]
    public void BuildReportHtml_WithoutSubscriptionWindow_OmitsFooter()
    {
        var noEnd = NewSponsor(1);
        noEnd.SubscriptionStart = new DateOnly(2025, 3, 1);
        noEnd.SubscriptionEnd = null;
        var noStart = NewSponsor(1);
        noStart.SubscriptionEnd = new DateOnly(2026, 9, 1);

        var htmlNoEnd = SponsorReportJob.BuildReportHtml(NewStats(1), noEnd, "February 2026", new Dictionary<int, string>(), new DateOnly(2026, 3, 5));
        var htmlNoStart = SponsorReportJob.BuildReportHtml(NewStats(1), noStart, "February 2026", new Dictionary<int, string>(), new DateOnly(2026, 3, 5));

        Assert.IsFalse(htmlNoEnd.Contains("remaining"));
        Assert.IsFalse(htmlNoStart.Contains("remaining"));
    }

    /// <summary>
    /// A sponsor with no per-event or per-source rows at all gets neither breakdown section.
    /// (The section guard runs before the event filter, which is why the filtered-to-empty case
    /// covered above still emits a header and an empty table.)
    /// </summary>
    [TestMethod]
    public void BuildReportHtml_WithNoBreakdowns_OmitsBothBreakdownSections()
    {
        var html = SponsorReportJob.BuildReportHtml(NewStats(1), NewSponsor(1), "February 2026", new Dictionary<int, string>(), new DateOnly(2026, 3, 5));

        Assert.IsFalse(html.Contains("Event Breakdown"));
        Assert.IsFalse(html.Contains("Source Breakdown"));
        StringAssert.Contains(html, "Overall Summary");
        StringAssert.Contains(html, "1,234", "Counts are thousands-separated.");
        StringAssert.Contains(html, "1 minute, 5 seconds");
    }

    /// <summary>Durations are rendered largest-unit-first, omitting zero components.</summary>
    [TestMethod]
    [DataRow(0L, "0 seconds")]
    [DataRow(-5L, "0 seconds")]
    [DataRow(999L, "0 seconds")]
    [DataRow(1000L, "1 second")]
    [DataRow(2000L, "2 seconds")]
    [DataRow(60_000L, "1 minute")]
    [DataRow(61_000L, "1 minute, 1 second")]
    [DataRow(3_600_000L, "1 hour")]
    [DataRow(3_601_000L, "1 hour, 1 second")]
    [DataRow(7_320_000L, "2 hours, 2 minutes")]
    [DataRow(86_400_000L, "1 day")]
    [DataRow(183_845_000L, "2 days, 3 hours, 4 minutes, 5 seconds")]
    public void FormatDuration_RendersLargestUnitFirstAndSkipsZeroComponents(long totalMs, string expected)
    {
        Assert.AreEqual(expected, SponsorReportJob.FormatDuration(totalMs));
    }

    #endregion

    #region Host lifecycle and failure

    /// <summary>
    /// The full hosted-service path: the job sends nothing until the host signals that it has
    /// started, then mails the reports and stops the host.
    /// </summary>
    [TestMethod]
    // The only thing that completes ExecuteTask is the ApplicationStarted token; if that setup ever
    // stops matching, the loose mock hands back a token that never cancels and the await never returns.
    [Timeout(30_000)]
    public async Task ExecuteAsync_WaitsForApplicationStartedThenSendsAndStopsHost()
    {
        var h = CreateHarness(applicationAlreadyStarted: false);
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(1));
            seed.SponsorStatistics.Add(NewStats(1));
            await seed.SaveChangesAsync();
        }

        var job = CreateJob(h, new ImmediateTimerTimeProvider(RunTime));
        await job.StartAsync(CancellationToken.None);
        Assert.IsNotNull(job.ExecuteTask);
        Assert.AreEqual(0, job.Sent.Count, "Nothing may be sent before the host has started.");

        await h.AppStarted.CancelAsync();
        await job.ExecuteTask;

        Assert.AreEqual(1, job.Sent.Count);
        h.Lifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);
    }

    /// <summary>
    /// A failure reaching the database propagates (so the pod exits non-zero) but the host is
    /// still stopped so the run does not hang.
    /// </summary>
    [TestMethod]
    public async Task RunReports_WhenCanceled_PropagatesAndStillStopsHost()
    {
        var h = CreateHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var job = CreateJob(h);
        await Assert.ThrowsAsync<OperationCanceledException>(() => job.RunReportsAsync(cts.Token));

        Assert.AreEqual(0, job.Sent.Count);
        h.Lifetime.Verify(l => l.StopApplication(), Times.Once);
    }

    #endregion
}
