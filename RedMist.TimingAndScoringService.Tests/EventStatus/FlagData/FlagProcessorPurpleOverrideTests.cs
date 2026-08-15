using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.FlagData;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.Tests.EventStatus.FlagData;

/// <summary>
/// The Flagtronics purple override as it reaches the flag log. RMonitor has no purple full-course
/// flag, so the relay's flag list shows a plain yellow for the whole caution; the log is what the
/// flags tab and the session archive read, so the purple has to be written into it the same way it
/// overrides the overall flag. See <see cref="SessionContext.GetEffectiveTrackFlag"/>.
/// </summary>
[TestClass]
public class FlagProcessorPurpleOverrideTests
{
    private const int EventId = 7;
    private const int SessionId = 21;

    /// <summary>
    /// The flag log is kept on the timing system's clock, not on UTC, so the times here are the
    /// wall clock the heartbeat reports rather than anything the server would produce.
    /// </summary>
    private static readonly DateTime YellowStart = new(2026, 8, 14, 13, 10, 0);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Reconcile_PurpleDuringAYellow_EndsTheYellowAndOpensAPurple()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();

        var updates = await ReportPurpleAsync(processor, context, from: "13:12:30");

        Assert.IsNotNull(updates, "Clients have to be told the list changed.");
        var logged = Logged(dbFactory);
        Assert.HasCount(2, logged);
        Assert.AreEqual(Flags.Yellow, logged[0].Flag);
        Assert.AreEqual(YellowStart, logged[0].StartTime);
        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 12, 29), logged[0].EndTime,
            "The yellow ends the second before the purple starts, as the relay's own entries do.");
        Assert.AreEqual(Flags.Purple35, logged[1].Flag);
        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 12, 30), logged[1].StartTime);
        Assert.IsNull(logged[1].EndTime, "The purple is still running.");
    }

    /// <summary>
    /// The boundary comes from the heartbeat's time of day, which is the clock the relay stamps its
    /// flags with. A server clock would be hours out - an observed production feed ran three hours
    /// from UTC - and would sort the purple to the wrong end of the list.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_DatesTheBoundaryOnTheTimingSystemClock()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();

        await ReportPurpleAsync(processor, context, from: "13:12:30");

        var purple = Logged(dbFactory).Single(f => f.Flag == Flags.Purple35);
        Assert.AreEqual(YellowStart.Date, purple.StartTime.Date, "The date comes from the entry being split.");
        Assert.AreEqual(new TimeSpan(13, 12, 30), purple.StartTime.TimeOfDay);
    }

    /// <summary>
    /// The change is held back until it has proven itself, but the entry still has to read as
    /// having started when the purple did rather than when it was confirmed.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_BackDatesTheBoundaryToWhenThePurpleWasFirstSeen()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();
        context.FlagtronicsFullCourseFlag = Flags.Purple35;

        context.SessionState.LocalTimeOfDay = "13:12:30";
        Assert.IsNull(await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken),
            "A single record is not yet enough to write to the log.");

        context.SessionState.LocalTimeOfDay = "13:12:41";
        await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken);

        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 12, 30), Logged(dbFactory)[1].StartTime);
    }

    /// <summary>
    /// The full-course flag is taken from whichever car reported one last in a message, so it
    /// flickers either side of a real change. The overall flag absorbs that because clients re-read
    /// it; an entry written into the log stays there, and a flapping feed would fill the flags tab
    /// with momentary entries and march the log's newest entry ahead of the timing system.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_APurpleThatDoesNotHold_IsNotLogged()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();

        // Purple over a run of records, then gone again well inside the confirm window.
        context.FlagtronicsFullCourseFlag = Flags.Purple35;
        foreach (var second in new[] { "30", "31", "32" })
        {
            context.SessionState.LocalTimeOfDay = $"13:12:{second}";
            Assert.IsNull(await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken));
        }

        context.FlagtronicsFullCourseFlag = Flags.Yellow;
        context.SessionState.LocalTimeOfDay = "13:12:33";
        Assert.IsNull(await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken));

        var logged = Logged(dbFactory);
        Assert.HasCount(1, logged, "A flicker leaves the caution as the relay logged it.");
        Assert.IsNull(logged[0].EndTime);
    }

    /// <summary>
    /// A feed alternating record by record, which is how the transition itself reads: each reading
    /// disagrees with the one before it, so nothing ever holds long enough to be logged.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_AFlappingFullCourseFlag_IsNotLogged()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();

        for (int second = 30; second < 45; second++)
        {
            context.SessionState.LocalTimeOfDay = $"13:12:{second:00}";
            context.FlagtronicsFullCourseFlag = second % 2 == 0 ? Flags.Purple35 : Flags.Yellow;
            Assert.IsNull(await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken));
        }

        Assert.HasCount(1, Logged(dbFactory));
    }

    [TestMethod]
    public async Task Reconcile_PurpleReleasedWhileStillUnderYellow_EndsThePurpleAndResumesTheYellow()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();
        await ReportPurpleAsync(processor, context, from: "13:12:30");

        // The slow zone lifts but the caution runs on.
        var updates = await ReportFullCourseFlagAsync(processor, context, Flags.Yellow, from: "13:20:00");

        Assert.IsNotNull(updates);
        var logged = Logged(dbFactory);
        Assert.HasCount(3, logged);
        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 19, 59), logged[1].EndTime);
        Assert.AreEqual(Flags.Yellow, logged[2].Flag);
        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 20, 0), logged[2].StartTime);
        Assert.IsNull(logged[2].EndTime, "The caution is still running.");
    }

    /// <summary>
    /// The override is only ever an upgrade of a yellow. Anything else the timing system is showing
    /// is its own to report, so the log is left alone.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_PurpleWhileTheTimingSystemIsGreen_LeavesTheLogAlone()
    {
        var (processor, context, dbFactory) = await CreateAsync();
        await SendFlagsAsync(processor, context, [new FlagDuration { Flag = Flags.Green, StartTime = YellowStart }]);
        context.RMonitorTrackFlag = Flags.Green;

        var updates = await ReportPurpleAsync(processor, context, from: "13:12:30");

        Assert.IsNull(updates);
        var logged = Logged(dbFactory);
        Assert.HasCount(1, logged);
        Assert.AreEqual(Flags.Green, logged[0].Flag);
        Assert.IsNull(logged[0].EndTime);
    }

    /// <summary>
    /// A timing system that sends its own purple flag has it logged by the relay like any other
    /// flag. That entry is the timing system's, and ending it here - or resuming a yellow the track
    /// is not under - would rewrite what the timing system actually reported.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_APurpleTheTimingSystemSentItself_IsNotTouched()
    {
        var (processor, context, dbFactory) = await CreateAsync();
        var purpleStart = new DateTime(2026, 8, 14, 13, 15, 0);
        await SendFlagsAsync(processor, context,
        [
            new FlagDuration { Flag = Flags.Yellow, StartTime = YellowStart, EndTime = purpleStart.AddSeconds(-1) },
            new FlagDuration { Flag = Flags.Purple35, StartTime = purpleStart },
        ]);
        context.RMonitorTrackFlag = Flags.Purple35;

        // Flagtronics is reporting the caution rather than the slow zone, which for an override
        // entry would mean releasing it.
        var updates = await ReportFullCourseFlagAsync(processor, context, Flags.Yellow, from: "13:20:00");

        Assert.IsNull(updates);
        var logged = Logged(dbFactory);
        Assert.HasCount(2, logged);
        Assert.AreEqual(Flags.Purple35, logged[1].Flag);
        Assert.IsNull(logged[1].EndTime, "The timing system's own purple is still running.");
    }

    /// <summary>
    /// The relay is the only source of the caution itself. Purple arriving before the relay has
    /// logged a yellow has nothing to override, and an entry written on its own would put a flag
    /// change in the log that the timing system never made.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_PurpleWithNoYellowLogged_WritesNothing()
    {
        var (processor, context, dbFactory) = await CreateAsync();
        await SendFlagsAsync(processor, context,
            [new FlagDuration { Flag = Flags.Yellow, StartTime = YellowStart, EndTime = YellowStart.AddMinutes(2) }]);
        context.RMonitorTrackFlag = Flags.Yellow;

        var updates = await ReportPurpleAsync(processor, context, from: "13:12:30");

        Assert.IsNull(updates);
        Assert.HasCount(1, Logged(dbFactory));
    }

    /// <summary>
    /// Records arrive several times a second and all but the first say the same thing. Acting on
    /// each would rewrite the log - and republish the whole list to every client - continuously.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_WhilePurpleContinues_ChangesNothingFurther()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();
        await ReportPurpleAsync(processor, context, from: "13:12:30");

        context.SessionState.LocalTimeOfDay = "13:12:45";
        var updates = await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken);

        Assert.IsNull(updates);
        Assert.HasCount(2, Logged(dbFactory));
    }

    /// <summary>
    /// Without a heartbeat there is no clock to place the boundary on, and the log stays as the
    /// relay left it rather than gaining an entry timed from the wrong clock.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_WithNoTimingSystemClock_LeavesTheLogAlone()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();
        context.FlagtronicsFullCourseFlag = Flags.Purple35;
        context.SessionState.LocalTimeOfDay = string.Empty;

        Assert.IsNull(await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken));
        Assert.IsNull(await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken));

        Assert.HasCount(1, Logged(dbFactory));
    }

    /// <summary>
    /// The caution ending is the relay's to report: its next flag closes the purple through the
    /// auto-complete, and no yellow is resumed under a green track.
    /// </summary>
    [TestMethod]
    public async Task Process_WhenTheCautionEndsDuringAPurple_TheRelaysNextFlagClosesIt()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();
        await ReportPurpleAsync(processor, context, from: "13:12:30");

        // The timing system goes green with the slow zone still reported.
        var greenStart = new DateTime(2026, 8, 14, 13, 25, 0);
        context.RMonitorTrackFlag = Flags.Green;
        await SendFlagsAsync(processor, context,
        [
            new FlagDuration { Flag = Flags.Yellow, StartTime = YellowStart, EndTime = greenStart.AddSeconds(-1) },
            new FlagDuration { Flag = Flags.Green, StartTime = greenStart },
        ]);

        var logged = Logged(dbFactory);
        Assert.HasCount(3, logged);
        Assert.AreEqual(Flags.Purple35, logged[1].Flag);
        Assert.AreEqual(greenStart, logged[1].EndTime, "The purple ends where the relay's green begins.");
        Assert.AreEqual(Flags.Green, logged[2].Flag);
        Assert.IsNull(logged[2].EndTime);
    }

    /// <summary>
    /// A caution called while the slow zone is already running: the relay logs a plain yellow and
    /// the purple takes over from it, however little of the caution was left underneath.
    /// </summary>
    [TestMethod]
    public async Task Process_AYellowLoggedWhilePurpleIsAlreadyReported_IsSplitFromItsStart()
    {
        var (processor, context, dbFactory) = await CreateAsync();
        context.FlagtronicsFullCourseFlag = Flags.Purple35;
        context.RMonitorTrackFlag = Flags.Yellow;

        context.SessionState.LocalTimeOfDay = "13:10:00";
        await SendFlagsAsync(processor, context, [new FlagDuration { Flag = Flags.Yellow, StartTime = YellowStart }]);
        context.SessionState.LocalTimeOfDay = "13:10:20";
        await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken);

        var logged = Logged(dbFactory);
        Assert.HasCount(2, logged);
        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 10, 0), logged[0].EndTime);
        Assert.AreEqual(Flags.Purple35, logged[1].Flag);
        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 10, 1), logged[1].StartTime,
            "The purple cannot start before the caution it is overriding.");
    }

    /// <summary>
    /// A restart part-way through an override leaves the purple entry open in the log with nothing
    /// in memory describing it. The processor has to pick it back up from the log, or the slow zone
    /// would run to the end of the caution and the yellow would never resume.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_AfterARestartDuringAnOverride_StillReleasesThePurple()
    {
        var (processor, context, dbFactory) = await CreateWithOpenYellowAsync();
        await ReportPurpleAsync(processor, context, from: "13:12:30");

        // A fresh processor over the same log, as a restarted event processor would have.
        var restarted = new FlagProcessor(dbFactory, new DebugLoggerFactory(), context);
        await SendFlagsAsync(restarted, context, [new FlagDuration { Flag = Flags.Yellow, StartTime = YellowStart }]);

        var updates = await ReportFullCourseFlagAsync(restarted, context, Flags.Yellow, from: "13:20:00");

        Assert.IsNotNull(updates);
        var logged = Logged(dbFactory);
        Assert.HasCount(3, logged);
        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 19, 59), logged[1].EndTime);
        Assert.AreEqual(Flags.Yellow, logged[2].Flag);
        Assert.AreEqual(new DateTime(2026, 8, 14, 13, 20, 0), logged[2].StartTime);
    }

    /// <summary>
    /// The purple entry has to reach the clients that are watching, not just the database the flags
    /// tab loads from.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_PublishesTheWholeListToTheSessionState()
    {
        var (processor, context, _) = await CreateWithOpenYellowAsync();

        var updates = await ReportPurpleAsync(processor, context, from: "13:12:30");

        Assert.IsNotNull(updates);
        Assert.HasCount(1, updates.SessionPatches);
        Assert.HasCount(2, updates.SessionPatches[0].FlagDurations!);
        Assert.HasCount(2, context.SessionState.FlagDurations);
        Assert.IsTrue(context.SessionState.FlagDurations.Any(f => f.Flag == Flags.Purple35));
    }

    /// <summary>
    /// Reports a Flagtronics full-course flag from the given time on the timing system's clock and
    /// runs the reconcile long enough for the change to be confirmed, as a run of records would.
    /// </summary>
    private async Task<PatchUpdates?> ReportFullCourseFlagAsync(FlagProcessor processor, SessionContext context,
        Flags fullCourseFlag, string from)
    {
        context.FlagtronicsFullCourseFlag = fullCourseFlag;
        context.SessionState.LocalTimeOfDay = from;
        await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken);

        // Ten seconds on, past the confirm window.
        var confirmed = TimeSpan.Parse(from) + TimeSpan.FromSeconds(10);
        context.SessionState.LocalTimeOfDay = confirmed.ToString(@"hh\:mm\:ss");
        return await processor.ReconcilePurpleOverrideAsync(TestContext.CancellationToken);
    }

    private Task<PatchUpdates?> ReportPurpleAsync(FlagProcessor processor, SessionContext context, string from)
        => ReportFullCourseFlagAsync(processor, context, Flags.Purple35, from);

    private static List<FlagLog> Logged(IDbContextFactory<TsContext> dbFactory)
    {
        using var db = dbFactory.CreateDbContext();
        return [.. db.FlagLog
            .Where(f => f.EventId == EventId && f.SessionId == SessionId)
            .OrderBy(f => f.StartTime)];
    }

    private static Task SendFlagsAsync(FlagProcessor processor, SessionContext context, List<FlagDuration> flags)
        => processor.Process(new TimingMessage(Backend.Shared.Consts.FLAGS_TYPE, JsonSerializer.Serialize(flags),
            context.SessionState.SessionId, DateTime.UtcNow));

    /// <summary>
    /// A session under a caution the relay has logged and not yet ended.
    /// </summary>
    private async Task<(FlagProcessor Processor, SessionContext Context, IDbContextFactory<TsContext> DbFactory)> CreateWithOpenYellowAsync()
    {
        var created = await CreateAsync();
        created.Context.SessionState.LocalTimeOfDay = "13:10:00";
        await SendFlagsAsync(created.Processor, created.Context,
            [new FlagDuration { Flag = Flags.Yellow, StartTime = YellowStart }]);
        created.Context.RMonitorTrackFlag = Flags.Yellow;
        return created;
    }

    private static Task<(FlagProcessor Processor, SessionContext Context, IDbContextFactory<TsContext> DbFactory)> CreateAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        IDbContextFactory<TsContext> dbFactory = new TestDbContextFactory(optionsBuilder.Options);
        var loggerFactory = new DebugLoggerFactory();
        var context = new SessionContext(configuration, dbFactory, loggerFactory, new Mock<ICarLapHistoryService>().Object);
        context.SessionState.SessionId = SessionId;
        return Task.FromResult((new FlagProcessor(dbFactory, loggerFactory, context), context, dbFactory));
    }
}
