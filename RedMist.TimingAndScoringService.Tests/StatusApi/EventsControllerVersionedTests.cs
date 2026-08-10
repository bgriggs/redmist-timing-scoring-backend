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
        var first = await _h.Controller.GetUIVersionInfoAsync();
        var second = await _h.Controller.GetUIVersionInfoAsync();

        Assert.AreEqual(first.LatestIOSVersion, second.LatestIOSVersion);
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

    [TestMethod]
    public async Task V2_LoadArchivedEvents_TakeAtLimit_IsAccepted()
    {
        var result = await _h.Controller.LoadArchivedEventsAsync(0, 100);

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task V2_LoadArchivedEvents_ReturnsOnlyArchivedNonDeletedEvents()
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
