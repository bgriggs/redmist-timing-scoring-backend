using RedMist.Backend.Shared.Utilities;
using RedMist.Database.Models;
using RedMist.PostEventReports.Email;
using RedMist.PostEventReports.Sections;
using RedMist.PostEventReports.Sections.Viewership;
using RedMist.PostEventReports.Suggestions;
using RedMist.TimingCommon.Models;
using System.Text.RegularExpressions;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.PostEventReports;

/// <summary>
/// Covers the markup the report is delivered as.
/// </summary>
/// <remarks>
/// Email clients are not browsers, and the ways they break are silent: the message renders, it just
/// renders wrong, in a client nobody on the team reads their mail in. These assertions stand in for
/// the manual check nobody will repeat on every change.
/// </remarks>
[TestClass]
public class PostEventReportRendererTests
{
    private static readonly DateTime Origin = new(2026, 9, 19, 14, 0, 0, DateTimeKind.Utc);

    private static Event NewEvent(string name = "Autumn Classic") => new()
    {
        Id = 400,
        OrganizationId = 7,
        Name = name,
        TrackName = "Portland",
        StartDate = Origin.AddDays(-1),
        EndDate = Origin,
    };

    private static Organization NewOrganization(string name = "Test Org") =>
        new() { Id = 7, ClientId = "relay-test", Name = name, ShortName = "TO" };

    private static EventViewershipSummary BuildSummary(int hours = 1, int? offsetMinutes = -300,
        string? sessionName = null, int racingSessions = 0, int viewers = 4)
    {
        var random = new Random(20260919);
        var sessions = new List<EventViewerSession>();
        for (var i = 0; i < viewers; i++)
        {
            var start = viewers > 8
                ? Origin.AddMinutes(random.Next(0, (int)TimeSpan.FromHours(hours).TotalMinutes))
                : Origin.AddMinutes(i * 3);
            sessions.Add(new EventViewerSession
            {
                EventId = 400,
                ConnectionId = $"c{i}",
                ClientType = ViewerClientTypes.Reported[i % ViewerClientTypes.Reported.Length],
                StartUtc = start,
                EndUtc = viewers > 8 ? start.AddMinutes(random.Next(2, 70)) : Origin.AddHours(hours).AddMinutes(-(i * 2)),
            });
        }

        Backend.Shared.Utilities.SessionWindow[] racing;
        if (racingSessions > 0)
        {
            var each = TimeSpan.FromHours(hours).TotalMinutes / racingSessions;
            racing = [.. Enumerable.Range(0, racingSessions).Select(i =>
                new Backend.Shared.Utilities.SessionWindow(i + 1, $"Session {i + 1}", i < racingSessions / 2,
                    Origin.AddMinutes(i * each), Origin.AddMinutes(((i + 1) * each) - 5)))];
        }
        else
        {
            racing = sessionName is null
                ? []
                : [new Backend.Shared.Utilities.SessionWindow(1, sessionName, false, Origin, Origin.AddHours(hours))];
        }

        return ViewershipAggregator.Aggregate(
            sessions,
            racing,
            offsetMinutes is { } m ? TimeSpan.FromMinutes(m) : null,
            Origin.AddDays(-2),
            Origin.AddDays(2),
            TimeSpan.FromHours(96));
    }

    private static string Render(EventViewershipSummary summary, Event? evt = null, Organization? org = null,
        IReadOnlyList<ReportSuggestion>? suggestions = null)
    {
        var section = new ReportSectionResult(ViewershipSection.SectionCode, "Viewership",
            ViewershipSectionRenderer.Render(summary), "Peak 4 watching");
        return PostEventReportRenderer.Render(evt ?? NewEvent(), org ?? NewOrganization(),
            [section], suggestions ?? []);
    }

    #region Escaping

    /// <summary>
    /// Event names are free text typed by people. An apostrophe or an ampersand is far more likely
    /// than not, and the existing sponsor report gets this wrong.
    /// </summary>
    [TestMethod]
    public void EventNameWithMarkupCharacters_IsEscaped()
    {
        var html = Render(BuildSummary(), NewEvent("Bob's \"Big\" <Race> & Rally"));

        Assert.DoesNotContain("<Race>", html);
        Assert.Contains("&lt;Race&gt;", html);
        Assert.Contains("&amp; Rally", html);
    }

    [TestMethod]
    public void OrganizationNameWithMarkupCharacters_IsEscaped()
    {
        var html = Render(BuildSummary(), org: NewOrganization("Smith & Sons <Racing>"));

        Assert.DoesNotContain("<Racing>", html);
        Assert.Contains("Smith &amp; Sons", html);
    }

    [TestMethod]
    public void SessionNameWithMarkupCharacters_IsEscaped()
    {
        var html = Render(BuildSummary(sessionName: "Race <1> & <2>"));

        Assert.DoesNotContain("<1>", html);
        Assert.Contains("&lt;1&gt;", html);
    }

    #endregion

    #region Email client constraints

    /// <summary>
    /// A literal check on the things that break Outlook's Word renderer. Cheap, and it catches the
    /// future improvement that looks fine in a browser and silently ruins the email.
    /// </summary>
    [TestMethod]
    [DataRow("<svg")]
    [DataRow("background-image")]
    [DataRow("display:flex")]
    [DataRow("display:grid")]
    [DataRow("position:absolute")]
    [DataRow("float:")]
    [DataRow("<script")]
    public void Output_ContainsNothingOutlookCannotRender(string forbidden)
    {
        Assert.DoesNotContain(forbidden, Render(BuildSummary()), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Word honours the bgcolor attribute and ignores the style; everything else does the reverse.
    /// A cell with only one of the two is invisible in one of them.
    /// </summary>
    [TestMethod]
    public void EveryCellWithABackgroundColorStyle_AlsoCarriesTheAttribute()
    {
        var html = Render(BuildSummary());

        foreach (Match cell in Regex.Matches(html, "<td[^>]*>"))
        {
            if (cell.Value.Contains("background-color:", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("bgcolor=", cell.Value,
                    $"This cell would lose its colour in Outlook: {cell.Value}");
            }
        }
    }

    /// <summary>
    /// The grey line the inbox shows beside the subject. Without one, Gmail shows whatever text the
    /// header happens to begin with.
    /// </summary>
    [TestMethod]
    public void Output_CarriesAHiddenPreheader()
    {
        Assert.Contains("mso-hide:all", Render(BuildSummary()));
    }

    [TestMethod]
    public void Output_DeclaresDarkModeSupportForOutlookComAsWellAsTheMediaQuery()
    {
        var html = Render(BuildSummary());

        Assert.Contains("prefers-color-scheme:dark", html);
        Assert.Contains("[data-ogsc]", html);
        Assert.Contains("[data-ogsb]", html);
    }

    #endregion

    #region The chart

    [TestMethod]
    public void BarSegmentWidths_SumToTheBarWidth()
    {
        var widths = EmailHtml.DistributePixels([3.2, 1.1, 0.4, 7.9], 100);

        Assert.AreEqual(100, widths.Sum());
    }

    /// <summary>
    /// A single API client disappearing from the chart entirely reads as a bug rather than as a
    /// small number.
    /// </summary>
    [TestMethod]
    public void AShareThatRoundsBelowOnePixel_StillGetsOne()
    {
        var widths = EmailHtml.DistributePixels([1000, 0.4], 100);

        Assert.AreEqual(1, widths[1]);
        Assert.AreEqual(100, widths.Sum());
    }

    [TestMethod]
    public void AZeroShare_GetsNoPixelsAtAll()
    {
        var widths = EmailHtml.DistributePixels([10, 0, 5], 60);

        Assert.AreEqual(0, widths[1]);
    }

    /// <summary>A zero-width cell renders as a one-pixel colour artifact in Outlook.</summary>
    [TestMethod]
    public void AZeroWidthSegment_IsNotEmittedAtAll()
    {
        Assert.AreEqual(string.Empty, EmailHtml.BarSegment(0, "#123456"));
    }

    [TestMethod]
    public void AShortEvent_IsChartedAtTheStoredFifteenMinuteResolution()
    {
        var summary = BuildSummary(hours: 2);

        var chart = ChartRollup.Build(summary.Buckets, sessionId: null);

        Assert.AreEqual(ViewershipAggregator.BucketLength, chart.Interval);
        Assert.HasCount(8, chart.Rows);
    }

    /// <summary>
    /// A twenty-four hour race is ninety-six buckets. Nobody reads ninety-six rows, and Gmail clips a
    /// message over roughly 102 KB.
    /// </summary>
    [TestMethod]
    public void ALongEvent_IsRolledUpUntilTheChartFits()
    {
        var summary = BuildSummary(hours: 24);

        var chart = ChartRollup.Build(summary.Buckets, sessionId: null);

        Assert.IsTrue(chart.Rows.Count <= ChartRollup.MaxRows,
            $"A 24 hour event produced {chart.Rows.Count} chart rows.");
        Assert.AreEqual(TimeSpan.FromHours(1), chart.Interval);
    }

    [TestMethod]
    public void ARolledUpRow_TakesTheLargestChildMaximum()
    {
        var summary = BuildSummary(hours: 24);

        var chart = ChartRollup.Build(summary.Buckets, sessionId: null);
        var stored = summary.Buckets
            .Where(b => b.SessionId == null && b.ClientType == ViewerClientTypes.All)
            .ToList();

        Assert.AreEqual(stored.Max(b => b.MaxConcurrent), chart.Rows.Max(r => r.Max));
    }

    /// <summary>
    /// The budget has to hold for a series at the maximum reportable window, not just for the ones a
    /// convenient fixture produces.
    /// </summary>
    /// <remarks>
    /// A 96-hour window is 384 buckets, which is 24 rows even at the widest listed interval - so a
    /// roll-up that fell back to that interval would silently ignore any budget below 24, and the
    /// report-wide budget that keeps the email under Gmail's clipping threshold would bound nothing.
    /// </remarks>
    [TestMethod]
    [DataRow(24)]
    [DataRow(16)]
    [DataRow(10)]
    [DataRow(6)]
    public void AFullWindowChart_HonoursWhateverRowBudgetItIsGiven(int maxRows)
    {
        var summary = BuildSummary(hours: 96, viewers: 200);

        var chart = ChartRollup.Build(summary.Buckets, sessionId: null, maxRows);

        Assert.IsTrue(chart.Rows.Count <= maxRows,
            $"Asked for at most {maxRows} rows and got {chart.Rows.Count} at {chart.Interval}.");
    }

    [TestMethod]
    public void ARolledUpChart_SaysWhatIntervalItIsShowing()
    {
        Assert.Contains("1-hour intervals", Render(BuildSummary(hours: 24)));
    }

    /// <summary>
    /// The test that justifies the roll-up. Without it, someone removes the roll-up and Gmail starts
    /// clipping the report at whatever row it reaches.
    /// </summary>
    [TestMethod]
    public void ATwentyFourHourReport_StaysWellUnderGmailsClippingThreshold()
    {
        var html = Render(BuildSummary(hours: 24, sessionName: "Enduro"));

        Assert.IsTrue(html.Length < 90_000,
            $"The rendered report is {html.Length / 1024}KB; Gmail clips at about 102KB.");
    }

    /// <summary>
    /// The case a per-chart limit does not cover. Ten sessions is eleven charts, each individually
    /// within its own limit and the report as a whole well past the point Gmail truncates it - which
    /// the reader sees as "[Message clipped]" and however much happened to fit.
    /// </summary>
    [TestMethod]
    public void AWeekendWithManySessions_StaysUnderTheThresholdToo()
    {
        var html = Render(BuildSummary(hours: 24, racingSessions: 10, viewers: 900));

        Assert.IsTrue(html.Length < 90_000,
            $"The rendered report is {html.Length / 1024}KB; Gmail clips at about 102KB.");
    }

    /// <summary>
    /// A session that did not earn a chart still has to have its figures somewhere, or a long weekend
    /// simply loses sessions off the report.
    /// </summary>
    [TestMethod]
    public void EverySession_AppearsInTheSessionTableEvenWhenItGetsNoChart()
    {
        var summary = BuildSummary(hours: 24, racingSessions: 10, viewers: 900);

        var html = Render(summary);

        foreach (var session in summary.Sessions)
        {
            Assert.Contains(session.SessionName, html,
                $"{session.SessionName} is missing from the report entirely.");
        }
    }

    [TestMethod]
    public void TheWholeReportsChartRows_StayWithinTheBudget()
    {
        var summary = BuildSummary(hours: 24, racingSessions: 10, viewers: 900);

        var html = Render(summary);

        // One bar table per chart row.
        var rows = Regex.Matches(html, "table-layout:fixed").Count;
        Assert.IsTrue(rows <= ChartRollup.MaxRowsPerReport,
            $"The report drew {rows} chart rows against a budget of {ChartRollup.MaxRowsPerReport}.");
    }

    #endregion

    #region Times

    [TestMethod]
    public void WithAKnownTrackOffset_TimesAreShownInTrackLocalTime()
    {
        var html = Render(BuildSummary(offsetMinutes: -300));

        Assert.Contains("09:00", html, "14:00 UTC is 09:00 at a track five hours behind.");
        Assert.DoesNotContain("UTC", html);
    }

    /// <summary>
    /// Presenting UTC as though it were local is worse than admitting the offset is unknown: an
    /// organizer reading a peak at 09:00 for a race that started at 14:00 concludes the feature is
    /// broken.
    /// </summary>
    [TestMethod]
    public void WithNoKnownTrackOffset_EveryTimeIsLabelledUtc()
    {
        var html = Render(BuildSummary(offsetMinutes: null));

        Assert.Contains("UTC", html);
    }

    #endregion

    #region Content

    [TestMethod]
    public void Suggestions_AreRenderedWithTheirTitleAndBody()
    {
        var html = Render(BuildSummary(), suggestions:
            [new ReportSuggestion("x", "Show your control log", "Events that publish their control log...")]);

        Assert.Contains("Ideas for next time", html);
        Assert.Contains("Show your control log", html);
    }

    [TestMethod]
    public void NoSuggestions_LeavesTheBlockOutEntirely()
    {
        Assert.DoesNotContain("Ideas for next time", Render(BuildSummary()));
    }

    /// <summary>
    /// Sessions are connections, and the report has to say so: one person's phone reconnecting on a
    /// network change produces several.
    /// </summary>
    [TestMethod]
    public void TheReport_SaysTheFiguresAreConnectionsRatherThanPeople()
    {
        Assert.Contains("connections", Render(BuildSummary()), StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void EachRacingSession_GetsItsOwnHeadingAndChart()
    {
        var html = Render(BuildSummary(sessionName: "Qualifying"));

        Assert.Contains("Qualifying", html);
        Assert.Contains("Across the whole event", html);
    }

    #endregion
}
