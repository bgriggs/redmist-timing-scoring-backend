#pragma warning disable CS0618
using Microsoft.AspNetCore.Mvc;
using RedMist.Database.Models;
using RedMist.TimingCommon;
using RedMist.TimingCommon.Models;
using PayloadEventStatus = RedMist.TimingCommon.Models.EventStatus;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Covers the version-specific endpoints. V1 and V2 read the same SessionResult row but each prefers
/// a different stored shape and converts the other, so the fallback rules decide what a client sees
/// for events archived by older or newer versions of the event processor.
/// </summary>
[TestClass]
public class EventsControllerVersionedTests
{
    private EventsControllerHarness _h = null!;

    [TestInitialize]
    public void Setup() => _h = new EventsControllerHarness();

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    private static Payload PayloadWithStatus(int eventId, string eventName = "From Payload") => new()
    {
        EventId = eventId,
        EventName = eventName,
        EventStatus = new PayloadEventStatus { EventId = eventId.ToString(), Flag = Flags.Checkered, RunningRaceTime = "02:00:00" },
        CarPositions = [new CarPosition { Number = "42", OverallPosition = 1 }],
    };

    private static SessionState StateWith(int eventId, int sessionId, string eventName = "From SessionState") => new()
    {
        EventId = eventId,
        SessionId = sessionId,
        EventName = eventName,
        CurrentFlag = Flags.Green,
        CarPositions = [new CarPosition { Number = "7", OverallPosition = 2 }],
    };

    #region V2 LoadSessionResults

    [TestMethod]
    public async Task V2_LoadSessionResults_NoRow_ReturnsNull()
    {
        Assert.IsNull(await _h.Controller.LoadSessionResults(7, 3));
    }

    /// <summary>
    /// Rows archived by the older processor only have the legacy Payload; V2 must synthesize a
    /// SessionState from it rather than returning null.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadSessionResults_LegacyPayloadOnly_IsConvertedToSessionState()
    {
        _h.Db.SessionResults.Add(new SessionResult { EventId = 7, SessionId = 3, Payload = PayloadWithStatus(7) });
        await _h.Db.SaveChangesAsync();

        var state = await _h.Controller.LoadSessionResults(7, 3);

        Assert.IsNotNull(state);
        Assert.AreEqual(7, state.EventId);
        Assert.AreEqual("From Payload", state.EventName);
        Assert.AreEqual(Flags.Checkered, state.CurrentFlag);
        Assert.AreEqual("42", state.CarPositions.Single().Number);
    }

    /// <summary>
    /// A Payload whose EventStatus carries no event id is the shape written before the status block
    /// was populated; it is treated as unusable and the stored SessionState wins.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadSessionResults_PayloadWithoutEventStatusId_FallsBackToStoredSessionState()
    {
        _h.Db.SessionResults.Add(new SessionResult
        {
            EventId = 7,
            SessionId = 3,
            Payload = new Payload { EventId = 7, EventStatus = new PayloadEventStatus { EventId = string.Empty } },
            SessionState = StateWith(7, 3),
        });
        await _h.Db.SaveChangesAsync();

        var state = await _h.Controller.LoadSessionResults(7, 3);

        Assert.IsNotNull(state);
        Assert.AreEqual("From SessionState", state.EventName);
        Assert.AreEqual(3, state.SessionId);
    }

    /// <summary>
    /// When both shapes are present, the legacy Payload is preferred - and the conversion drops the
    /// session id, so the returned state carries 0 rather than the stored session id.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadSessionResults_BothShapesPresent_PrefersConvertedPayload()
    {
        _h.Db.SessionResults.Add(new SessionResult
        {
            EventId = 7,
            SessionId = 3,
            Payload = PayloadWithStatus(7),
            SessionState = StateWith(7, 3),
        });
        await _h.Db.SaveChangesAsync();

        var state = await _h.Controller.LoadSessionResults(7, 3);

        Assert.IsNotNull(state);
        Assert.AreEqual("From Payload", state.EventName);
        Assert.AreEqual(0, state.SessionId, "the Payload has no session id to convert from");
    }

    [TestMethod]
    public async Task V2_LoadSessionResults_SessionStateOnly_ReturnsItUnchanged()
    {
        _h.Db.SessionResults.Add(new SessionResult { EventId = 7, SessionId = 3, SessionState = StateWith(7, 3) });
        await _h.Db.SaveChangesAsync();

        var state = await _h.Controller.LoadSessionResults(7, 3);

        Assert.IsNotNull(state);
        Assert.AreEqual("From SessionState", state.EventName);
        Assert.AreEqual(3, state.SessionId);
    }

    [TestMethod]
    public async Task V2_LoadSessionResults_MatchesOnBothEventAndSession()
    {
        _h.Db.SessionResults.AddRange(
            new SessionResult { EventId = 7, SessionId = 3, SessionState = StateWith(7, 3, "Session3") },
            new SessionResult { EventId = 7, SessionId = 4, SessionState = StateWith(7, 4, "Session4") });
        await _h.Db.SaveChangesAsync();

        Assert.AreEqual("Session4", (await _h.Controller.LoadSessionResults(7, 4))!.EventName);
        Assert.IsNull(await _h.Controller.LoadSessionResults(8, 3));
    }

    #endregion

    #region V2 GetUIVersionInfoAsync

    /// <summary>
    /// An empty table must yield a default record rather than null: the mobile apps compare their own
    /// version against these strings on startup and a null would break the check.
    /// </summary>
    [TestMethod]
    public async Task V2_GetUIVersionInfo_EmptyTable_ReturnsDefaultInsteadOfNull()
    {
        var info = await _h.Controller.GetUIVersionInfoAsync();

        Assert.IsNotNull(info);
        Assert.AreEqual(string.Empty, info.LatestIOSVersion);
    }

    /// <summary>
    /// Every app start hits this endpoint, so it must be answered from cache after the first miss.
    /// </summary>
    [TestMethod]
    public async Task V2_GetUIVersionInfo_SecondCall_ServedFromCache()
    {
        await _h.Controller.GetUIVersionInfoAsync();
        await _h.Controller.GetUIVersionInfoAsync();

        Assert.AreEqual(1, _h.Cache.FactoryInvocations.Count(k => k == "ui-version-info"));
    }

    #endregion

    #region V2 LoadArchivedEventsAsync

    private static List<EventListSummary> Summaries(ActionResult<List<EventListSummary>> result) =>
        (List<EventListSummary>)((OkObjectResult)result.Result!).Value!;

    /// <summary>The page-size cap is what stops a client pulling the whole archive in one query.</summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_TakeAbove100_ReturnsBadRequest()
    {
        var result = await _h.Controller.LoadArchivedEventsAsync(0, 101);

        var bad = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(bad);
        Assert.AreEqual("Take parameter exceeds maximum of 100", bad.Value);
    }

    /// <summary>
    /// PostgreSQL rejects a negative OFFSET or LIMIT, so these must not reach the query. An empty page
    /// is refused as well, matching the documented range.
    /// </summary>
    [TestMethod]
    [DataRow(-1, 10, "Offset parameter must not be negative")]
    [DataRow(0, 0, "Take parameter must be at least 1")]
    [DataRow(0, -5, "Take parameter must be at least 1")]
    public async Task V2_LoadArchivedEvents_OutOfRangePaging_ReturnsBadRequest(int offset, int take, string message)
    {
        var result = await _h.Controller.LoadArchivedEventsAsync(offset, take);

        var bad = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(bad);
        Assert.AreEqual(message, bad.Value);
    }

    [TestMethod]
    public async Task V2_LoadArchivedEvents_TakeAtLimit_IsAccepted()
    {
        var result = await _h.Controller.LoadArchivedEventsAsync(0, 100);

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
    }

    /// <summary>"NotArchived" is still in the recent list, so it must not show in this one too.</summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_ReturnsArchivedEventsButNotDeletedOrRecentOnes()
    {
        _h.AddOrganization(1);
        _h.AddEvent("Archived", isArchived: true);
        _h.AddEvent("ArchivedButDeleted", isArchived: true, isDeleted: true);
        _h.AddEvent("NotArchived");
        await _h.Db.SaveChangesAsync();

        var events = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 50));

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("Archived", events[0].EventName);
        Assert.IsTrue(events[0].IsArchived);
        Assert.IsFalse(events[0].IsLive);
    }

    /// <summary>
    /// The gap this list exists to close: with archiving behind or switched off, an unarchived event
    /// older than the 25 the recent list holds was in neither list. It belongs here, in date order
    /// among the archived ones, and says it is not archived.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_IncludesUnarchivedEventsPastTheRecentCap()
    {
        _h.AddOrganization(1);
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _h.AddEvent("OldArchived", startDate: baseDate.AddDays(-10), isArchived: true);
        for (int i = 0; i < 28; i++)
            _h.AddEvent($"Event{i:00}", startDate: baseDate.AddDays(i));
        // Archived but newer than every unarchived event: date order, not archive state, places it.
        _h.AddEvent("NewArchived", startDate: baseDate.AddDays(100), isArchived: true);
        await _h.Db.SaveChangesAsync();

        var events = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 50));

        // Event27..Event03 are the 25 recent ones; Event02..Event00 fell past the cap.
        CollectionAssert.AreEqual(
            new[] { "NewArchived", "Event02", "Event01", "Event00", "OldArchived" },
            events.Select(e => e.EventName).ToArray());
        CollectionAssert.AreEqual(
            new[] { true, false, false, false, true },
            events.Select(e => e.IsArchived).ToArray());
    }

    /// <summary>
    /// Being old enough to fall past the recent cap does not let a live or deleted event in. Deleted
    /// ones show nowhere. A live one is left out even when it is not the active event
    /// ("OldStaleLive"), the same as in the recent list: it shows in no list until its relay
    /// disconnects and orchestration clears IsLive. A simulation is listed like any other past event,
    /// matching the recent list.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_UnarchivedPastTheCap_LeavesOutLiveAndDeleted()
    {
        _h.AddOrganization(1);
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 25; i++)
            _h.AddEvent($"Recent{i:00}", startDate: baseDate.AddDays(i));
        var old = baseDate.AddDays(-30);
        _h.AddEvent("OldLive", startDate: old, isLive: true, isActive: true);
        _h.AddEvent("OldStaleLive", startDate: old, isLive: true, isActive: false);
        _h.AddEvent("OldDeleted", startDate: old, isDeleted: true);
        _h.AddEvent("OldSimulation", startDate: old, isSimulation: true);
        await _h.Db.SaveChangesAsync();

        var events = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 50));

        CollectionAssert.AreEqual(new[] { "OldSimulation" }, events.Select(e => e.EventName).ToArray());
        Assert.IsTrue(events[0].IsSimulation);
    }

    /// <summary>
    /// The recent and completed lists are two separate queries splitting one ordering. Events that
    /// start the same day are common, and if the two queries broke that tie differently an event at
    /// the cap could show in both lists or neither. Paged through, the two cover every past event
    /// exactly once.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_WithRecentList_CoversEveryEventOnce_WhenStartDatesTie()
    {
        _h.AddOrganization(1);
        var sameDay = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 40; i++)
            _h.AddEvent($"Event{i:00}", startDate: sameDay, isArchived: i % 4 == 0);
        await _h.Db.SaveChangesAsync();
        var expected = _h.Db.Events.Select(e => e.Id).ToList();

        var recent = await _h.Controller.LoadLiveAndRecentEvents();
        var completed = new List<EventListSummary>();
        for (int offset = 0; ; offset += 7)
        {
            var page = Summaries(await _h.Controller.LoadArchivedEventsAsync(offset, 7));
            completed.AddRange(page);
            if (page.Count < 7)
                break;
        }

        Assert.AreEqual(25, recent.Count);
        var listed = recent.Concat(completed).Select(e => e.Id).ToList();
        Assert.AreEqual(listed.Count, listed.Distinct().Count(), "an event was listed twice");
        CollectionAssert.AreEquivalent(expected, listed);
        // Ties fall to the higher Id first, in both lists.
        CollectionAssert.AreEqual(recent.Select(e => e.Id).OrderByDescending(id => id).ToArray(),
            recent.Select(e => e.Id).ToArray());
        CollectionAssert.AreEqual(completed.Select(e => e.Id).OrderByDescending(id => id).ToArray(),
            completed.Select(e => e.Id).ToArray());
    }

    /// <summary>
    /// An event going live or ending must not move the recent cap. If it did, every event in the
    /// completed list would slide along a place, and a client paging through it would skip or repeat
    /// one. The live event still counts toward the cap; the recent list just doesn't show it.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_AnEventGoingLiveDoesNotMoveTheRecentCap()
    {
        _h.AddOrganization(1);
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = new List<RedMist.TimingCommon.Models.Configuration.Event>();
        for (int i = 0; i < 30; i++)
            events.Add(_h.AddEvent($"Event{i:00}", startDate: baseDate.AddDays(i)));
        await _h.Db.SaveChangesAsync();
        var before = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 50)).Select(e => e.EventName).ToArray();

        events[29].IsLive = true;
        events[29].IsActive = true;
        await _h.Db.SaveChangesAsync();
        var after = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 50)).Select(e => e.EventName).ToArray();
        var recent = await _h.Controller.LoadLiveAndRecentEvents();

        CollectionAssert.AreEqual(new[] { "Event04", "Event03", "Event02", "Event01", "Event00" }, before);
        CollectionAssert.AreEqual(before, after);
        Assert.AreEqual("Event29", recent[0].EventName);
        Assert.IsTrue(recent[0].IsLive);
        Assert.AreEqual(24, recent.Count(e => !e.IsLive), "the live event keeps its place in the cap");
    }

    /// <summary>
    /// Upcoming events sort first, so with more than 25 of them the soonest would fall past the cap.
    /// They have not run yet, so they must not be listed as completed.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_LeavesOutUpcomingEventsPastTheRecentCap()
    {
        _h.AddOrganization(1);
        var tomorrow = DateTime.UtcNow.Date.AddDays(1);
        // Saved without an end date, and added first so the lowest Id puts it past the cap: only its
        // start date says it is upcoming
        _h.AddEvent("UpcomingNoEndDate", startDate: tomorrow);
        for (int i = 0; i < 26; i++)
            _h.AddEvent($"Upcoming{i:00}", startDate: tomorrow.AddDays(i), endDate: tomorrow.AddDays(i + 1));
        _h.AddEvent("Past", startDate: tomorrow.AddDays(-30), endDate: tomorrow.AddDays(-29));
        await _h.Db.SaveChangesAsync();

        var events = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 50));
        var recent = await _h.Controller.LoadLiveAndRecentEvents();

        CollectionAssert.AreEqual(new[] { "Past" }, events.Select(e => e.EventName).ToArray());
        // Not under way either, so the recent list does not take them in its place
        Assert.IsFalse(recent.Any(e => e.EventName is "Upcoming00" or "UpcomingNoEndDate"));
    }

    /// <summary>
    /// The end date is when the last day begins, so an event on its final day has not finished: it
    /// stays in the recent list until that day is over, then moves here. Together the two lists
    /// still show it exactly once.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_EventOnItsFinalDay_IsRecentUntilTheDayIsOver()
    {
        _h.AddOrganization(1);
        var today = DateTime.UtcNow.Date;
        for (int i = 0; i < 25; i++)
            _h.AddEvent($"Upcoming{i:00}", startDate: today.AddDays(i + 1), endDate: today.AddDays(i + 2));
        _h.AddEvent("FinalDay", startDate: today.AddDays(-1), endDate: today);
        _h.AddEvent("EndedYesterday", startDate: today.AddDays(-2), endDate: today.AddDays(-1));
        await _h.Db.SaveChangesAsync();

        var completed = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 50));
        var recent = await _h.Controller.LoadLiveAndRecentEvents();

        CollectionAssert.AreEqual(new[] { "EndedYesterday" }, completed.Select(e => e.EventName).ToArray());
        Assert.AreEqual(1, recent.Count(e => e.EventName == "FinalDay"));
        Assert.AreEqual(0, recent.Count(e => e.EventName == "EndedYesterday"));
    }

    /// <summary>
    /// A live event takes a place in the recent cap, so on a busy weekend it can push an event that
    /// is under way, but whose relay is not connected, past the cap. That event has not ended, so the
    /// completed list will not take it; the recent list must, or it would show nowhere.
    /// </summary>
    [TestMethod]
    public async Task LoadLiveAndRecentEvents_KeepsAnEventUnderWayThatALiveEventPushedPastTheCap()
    {
        _h.AddOrganization(1);
        var today = DateTime.UtcNow.Date;
        for (int i = 0; i < 24; i++)
            _h.AddEvent($"Upcoming{i:00}", startDate: today.AddDays(i + 1), endDate: today.AddDays(i + 2));
        var notLive = _h.AddEvent("UnderWayNotLive", startDate: today, endDate: today.AddDays(1));
        await _h.Db.SaveChangesAsync();
        // Added second so its higher Id wins the same-day tie and takes the last place in the cap
        _h.AddEvent("UnderWayLive", startDate: today, endDate: today.AddDays(1), isLive: true, isActive: true);
        await _h.Db.SaveChangesAsync();

        var recent = await _h.Controller.LoadLiveAndRecentEvents();
        var completed = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 50));

        Assert.AreEqual(1, recent.Count(e => e.Id == notLive.Id));
        Assert.IsFalse(recent.Single(e => e.Id == notLive.Id).IsLive);
        Assert.AreEqual(0, completed.Count);
    }

    /// <summary>
    /// An event keeps its Results button when it moves from the recent list to this one. Archiving
    /// keeps an event's session results, so this holds for archived events too.
    /// </summary>
    [TestMethod]
    public async Task V2_LoadArchivedEvents_SetsHasResults()
    {
        _h.AddOrganization(1);
        var withResults = _h.AddEvent("WithResults", isArchived: true);
        _h.AddEvent("WithoutResults", isArchived: true);
        await _h.Db.SaveChangesAsync();
        _h.AddSession(1, withResults.Id);
        await _h.Db.SaveChangesAsync();

        var events = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 10));

        Assert.IsTrue(events.Single(e => e.EventName == "WithResults").HasResults);
        Assert.IsFalse(events.Single(e => e.EventName == "WithoutResults").HasResults);
    }

    [TestMethod]
    public async Task V2_LoadArchivedEvents_OffsetAndTakePaginateNewestFirst()
    {
        _h.AddOrganization(1);
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++)
            _h.AddEvent($"Event{i}", startDate: baseDate.AddDays(i), isArchived: true);
        await _h.Db.SaveChangesAsync();

        var page = Summaries(await _h.Controller.LoadArchivedEventsAsync(1, 2));

        CollectionAssert.AreEqual(new[] { "Event3", "Event2" }, page.Select(e => e.EventName).ToArray());
    }

    [TestMethod]
    public async Task V2_LoadArchivedEvents_OffsetPastEnd_ReturnsEmptyList()
    {
        _h.AddOrganization(1);
        _h.AddEvent("Archived", isArchived: true);
        await _h.Db.SaveChangesAsync();

        var events = Summaries(await _h.Controller.LoadArchivedEventsAsync(10, 10));

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task V2_LoadArchivedEvents_HideName_BlanksName()
    {
        _h.AddOrganization(1);
        _h.AddEvent("Secret", isArchived: true, hideName: true);
        await _h.Db.SaveChangesAsync();

        var events = Summaries(await _h.Controller.LoadArchivedEventsAsync(0, 10));

        Assert.AreEqual(string.Empty, events[0].EventName);
        Assert.IsTrue(events[0].HideName);
    }

    #endregion

    #region V1 LoadSessionResults

    [TestMethod]
    public async Task V1_LoadSessionResults_NoRow_ReturnsNull()
    {
        Assert.IsNull(await _h.CreateV1Controller().LoadSessionResults(7, 3));
    }

    /// <summary>V1 prefers the modern SessionState and converts it down to the legacy Payload shape.</summary>
    [TestMethod]
    public async Task V1_LoadSessionResults_SessionStateWithSessionId_IsConvertedToPayload()
    {
        _h.Db.SessionResults.Add(new SessionResult
        {
            EventId = 7,
            SessionId = 3,
            SessionState = StateWith(7, 3),
            Payload = PayloadWithStatus(7),
        });
        await _h.Db.SaveChangesAsync();

        var payload = await _h.CreateV1Controller().LoadSessionResults(7, 3);

        Assert.IsNotNull(payload);
        Assert.AreEqual("From SessionState", payload.EventName);
        Assert.AreEqual(Flags.Green, payload.EventStatus!.Flag);
        Assert.AreEqual("7", payload.CarPositions!.Single().Number);
    }

    /// <summary>
    /// The conversion is gated on <c>SessionId &gt; 0</c>, so a genuine session 0 - which the timing
    /// feed does emit - falls through to the stored legacy Payload instead of the SessionState.
    /// </summary>
    [TestMethod]
    public async Task V1_LoadSessionResults_SessionIdZero_FallsBackToStoredPayload()
    {
        _h.Db.SessionResults.Add(new SessionResult
        {
            EventId = 7,
            SessionId = 0,
            SessionState = StateWith(7, 0),
            Payload = PayloadWithStatus(7),
        });
        await _h.Db.SaveChangesAsync();

        var payload = await _h.CreateV1Controller().LoadSessionResults(7, 0);

        Assert.IsNotNull(payload);
        Assert.AreEqual("From Payload", payload.EventName);
    }

    [TestMethod]
    public async Task V1_LoadSessionResults_PayloadOnly_ReturnsItUnchanged()
    {
        _h.Db.SessionResults.Add(new SessionResult { EventId = 7, SessionId = 3, Payload = PayloadWithStatus(7) });
        await _h.Db.SaveChangesAsync();

        var payload = await _h.CreateV1Controller().LoadSessionResults(7, 3);

        Assert.IsNotNull(payload);
        Assert.AreEqual("From Payload", payload.EventName);
        Assert.AreEqual(Flags.Checkered, payload.EventStatus!.Flag);
    }

    #endregion
}
#pragma warning restore CS0618
