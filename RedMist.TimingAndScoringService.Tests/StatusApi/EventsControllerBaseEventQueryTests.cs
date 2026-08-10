using Microsoft.AspNetCore.Mvc;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Covers the event listing/lookup surface of EventsControllerBase: the visibility rules that decide
/// which events a public client may see (deleted, archived, live, hide-name), the DTO shaping the
/// apps depend on, and the live-events cache.
/// </summary>
[TestClass]
public class EventsControllerBaseEventQueryTests
{
    private EventsControllerHarness _h = null!;

    [TestInitialize]
    public void Setup() => _h = new EventsControllerHarness();

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    private static List<Event> Events(ActionResult<List<Event>> result) =>
        (List<Event>)((OkObjectResult)result.Result!).Value!;

    #region LoadEvents

    /// <summary>
    /// The range cap is the only thing stopping a client from asking for the entire event history in
    /// one unindexed scan, so it has to reject rather than silently clamp.
    /// </summary>
    [TestMethod]
    public async Task LoadEvents_StartDateOlderThan30Days_ReturnsBadRequest()
    {
        var result = await _h.Controller.LoadEvents(DateTime.UtcNow.AddDays(-31));

        var bad = result.Result as BadRequestObjectResult;
        Assert.IsNotNull(bad);
        Assert.AreEqual("startDateUtc exceeds maximum range of 30 days", bad.Value);
    }

    [TestMethod]
    public async Task LoadEvents_StartDateInsideRange_IsAccepted()
    {
        var result = await _h.Controller.LoadEvents(DateTime.UtcNow.AddDays(-29));

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
    }

    /// <summary>A future start date is well inside the 30-day window and must not be rejected.</summary>
    [TestMethod]
    public async Task LoadEvents_FutureStartDate_IsAccepted()
    {
        var result = await _h.Controller.LoadEvents(DateTime.UtcNow.AddDays(5));

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task LoadEvents_ExcludesDeletedEventsAndEventsBeforeStartDate()
    {
        var start = DateTime.UtcNow.AddDays(-10);
        _h.AddOrganization(1);
        _h.AddEvent("Included", startDate: start.AddDays(1));
        _h.AddEvent("Deleted", startDate: start.AddDays(1), isDeleted: true);
        _h.AddEvent("TooOld", startDate: start.AddDays(-1));
        await _h.Db.SaveChangesAsync();

        var events = Events(await _h.Controller.LoadEvents(start));

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("Included", events[0].EventName);
    }

    /// <summary>
    /// HideName blanks the name in the response but leaves the flag set, so clients can render their
    /// own placeholder rather than an empty string.
    /// </summary>
    [TestMethod]
    public async Task LoadEvents_HideName_BlanksNameButKeepsFlag()
    {
        var start = DateTime.UtcNow.AddDays(-10);
        _h.AddOrganization(1);
        _h.AddEvent("Secret Event", startDate: start.AddDays(1), hideName: true);
        await _h.Db.SaveChangesAsync();

        var evt = Events(await _h.Controller.LoadEvents(start)).Single();

        Assert.AreEqual(string.Empty, evt.EventName);
        Assert.IsTrue(evt.HideName);
    }

    [TestMethod]
    public async Task LoadEvents_HasControlLog_ReflectsOrganizationControlLogType()
    {
        var start = DateTime.UtcNow.AddDays(-10);
        _h.AddOrganization(1, name: "With Log", controlLogType: "GoogleSheets", clientId: "relay-1");
        _h.AddOrganization(2, name: "Without Log", controlLogType: string.Empty, clientId: "relay-2");
        _h.AddEvent("A", organizationId: 1, startDate: start.AddDays(1));
        _h.AddEvent("B", organizationId: 2, startDate: start.AddDays(1));
        await _h.Db.SaveChangesAsync();

        var events = Events(await _h.Controller.LoadEvents(start));

        Assert.IsTrue(events.Single(e => e.EventName == "A").HasControlLog);
        Assert.IsFalse(events.Single(e => e.EventName == "B").HasControlLog);
    }

    /// <summary>
    /// The organization join is an inner join, so an event whose organization row is missing drops out
    /// of the listing entirely instead of surfacing with a blank organization.
    /// </summary>
    [TestMethod]
    public async Task LoadEvents_EventWithNoMatchingOrganization_IsOmitted()
    {
        var start = DateTime.UtcNow.AddDays(-10);
        _h.AddOrganization(1);
        _h.AddEvent("Orphan", organizationId: 99, startDate: start.AddDays(1));
        _h.AddEvent("Owned", organizationId: 1, startDate: start.AddDays(1));
        await _h.Db.SaveChangesAsync();

        var events = Events(await _h.Controller.LoadEvents(start));

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("Owned", events[0].EventName);
    }

    [TestMethod]
    public async Task LoadEvents_AttachesOnlyItsOwnSessions()
    {
        var start = DateTime.UtcNow.AddDays(-10);
        _h.AddOrganization(1);
        var a = _h.AddEvent("A", startDate: start.AddDays(1));
        var b = _h.AddEvent("B", startDate: start.AddDays(1));
        await _h.Db.SaveChangesAsync();
        _h.AddSession(1, a.Id, "A-Practice");
        _h.AddSession(2, a.Id, "A-Race");
        _h.AddSession(1, b.Id, "B-Race");
        await _h.Db.SaveChangesAsync();

        var events = Events(await _h.Controller.LoadEvents(start));

        CollectionAssert.AreEquivalent(new[] { "A-Practice", "A-Race" },
            events.Single(e => e.EventName == "A").Sessions.Select(s => s.Name).ToArray());
        Assert.AreEqual("B-Race", events.Single(e => e.EventName == "B").Sessions.Single().Name);
    }

    [TestMethod]
    public async Task LoadEvents_EmptyDatabase_ReturnsEmptyList()
    {
        var events = Events(await _h.Controller.LoadEvents(DateTime.UtcNow.AddDays(-1)));

        Assert.AreEqual(0, events.Count);
    }

    #endregion

    #region LoadLiveEvents

    [TestMethod]
    public async Task LoadLiveEvents_ReturnsOnlyActiveLiveNonDeletedEvents()
    {
        _h.AddOrganization(1);
        _h.AddEvent("Live", isLive: true, isActive: true);
        _h.AddEvent("NotActive", isLive: true, isActive: false);
        _h.AddEvent("NotLive", isLive: false, isActive: true);
        _h.AddEvent("Deleted", isLive: true, isActive: true, isDeleted: true);
        await _h.Db.SaveChangesAsync();

        var live = await _h.Controller.LoadLiveEvents();

        Assert.AreEqual(1, live.Count);
        Assert.AreEqual("Live", live[0].EventName);
        Assert.IsTrue(live[0].IsLive);
    }

    /// <summary>HasResults drives whether the apps show a "Results" button, so it must track the SessionResults rows.</summary>
    [TestMethod]
    public async Task LoadLiveEvents_HasResults_OnlyTrueWhenSessionResultExists()
    {
        _h.AddOrganization(1);
        var withResults = _h.AddEvent("WithResults", isLive: true, isActive: true);
        _h.AddEvent("WithoutResults", isLive: true, isActive: true);
        await _h.Db.SaveChangesAsync();
        _h.Db.SessionResults.Add(new SessionResult { EventId = withResults.Id, SessionId = 1 });
        await _h.Db.SaveChangesAsync();

        var live = await _h.Controller.LoadLiveEvents();

        Assert.IsTrue(live.Single(e => e.EventName == "WithResults").HasResults);
        Assert.IsFalse(live.Single(e => e.EventName == "WithoutResults").HasResults);
    }

    [TestMethod]
    public async Task LoadLiveEvents_FormatsEventDateAsIsoDay()
    {
        _h.AddOrganization(1);
        _h.AddEvent("Live", isLive: true, isActive: true,
            startDate: new DateTime(2026, 3, 7, 18, 45, 0, DateTimeKind.Utc));
        await _h.Db.SaveChangesAsync();

        var live = await _h.Controller.LoadLiveEvents();

        Assert.AreEqual("2026-03-07", live[0].EventDate);
    }

    [TestMethod]
    public async Task LoadLiveEvents_HideName_BlanksName()
    {
        _h.AddOrganization(1);
        _h.AddEvent("Secret", isLive: true, isActive: true, hideName: true);
        await _h.Db.SaveChangesAsync();

        var live = await _h.Controller.LoadLiveEvents();

        Assert.AreEqual(string.Empty, live[0].EventName);
        Assert.IsTrue(live[0].HideName);
    }

    /// <summary>
    /// The live-events list is polled by every landing page visitor, so it must be served from cache
    /// after the first miss rather than re-running the grouped query.
    /// </summary>
    [TestMethod]
    public async Task LoadLiveEvents_SecondCall_ServedFromCacheWithoutRequeryingDatabase()
    {
        _h.AddOrganization(1);
        _h.AddEvent("First", isLive: true, isActive: true);
        await _h.Db.SaveChangesAsync();

        var first = await _h.Controller.LoadLiveEvents();

        _h.AddEvent("AddedAfterFirstCall", isLive: true, isActive: true);
        await _h.Db.SaveChangesAsync();
        var second = await _h.Controller.LoadLiveEvents();

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count, "second call should have come from the cache, not the database");
        Assert.AreEqual(1, _h.Cache.FactoryInvocations.Count(k => k == "live-events"));
    }

    #endregion

    #region LoadLiveAndRecentEvents

    [TestMethod]
    public async Task LoadLiveAndRecentEvents_ReturnsLiveEventsBeforeRecentOnes()
    {
        _h.AddOrganization(1);
        _h.AddEvent("LiveOne", isLive: true, isActive: true);
        _h.AddEvent("RecentOne", isLive: false);
        await _h.Db.SaveChangesAsync();

        var results = await _h.Controller.LoadLiveAndRecentEvents();

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("LiveOne", results[0].EventName);
        Assert.IsTrue(results[0].IsLive);
        Assert.AreEqual("RecentOne", results[1].EventName);
        Assert.IsFalse(results[1].IsLive);
    }

    [TestMethod]
    public async Task LoadLiveAndRecentEvents_RecentExcludesArchivedDeletedAndLive()
    {
        _h.AddOrganization(1);
        _h.AddEvent("Recent");
        _h.AddEvent("Archived", isArchived: true);
        _h.AddEvent("Deleted", isDeleted: true);
        _h.AddEvent("Live", isLive: true, isActive: false);
        await _h.Db.SaveChangesAsync();

        var results = await _h.Controller.LoadLiveAndRecentEvents();

        // "Live" is IsLive but not IsActive, so it appears in neither list.
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("Recent", results[0].EventName);
    }

    /// <summary>
    /// The recent list is capped at 25 and ordered newest-first; without the ordering the cap would
    /// return an arbitrary slice of history.
    /// </summary>
    [TestMethod]
    public async Task LoadLiveAndRecentEvents_CapsRecentAt25NewestFirst()
    {
        _h.AddOrganization(1);
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 30; i++)
            _h.AddEvent($"Event{i:00}", startDate: baseDate.AddDays(i));
        await _h.Db.SaveChangesAsync();

        var results = await _h.Controller.LoadLiveAndRecentEvents();

        Assert.AreEqual(25, results.Count);
        Assert.AreEqual("Event29", results[0].EventName);
        Assert.AreEqual("Event05", results[24].EventName);
    }

    #endregion

    #region LoadEvent

    [TestMethod]
    public async Task LoadEvent_UnknownId_ReturnsNotFound()
    {
        var result = await _h.Controller.LoadEvent(12345);

        var notFound = result.Result as NotFoundObjectResult;
        Assert.IsNotNull(notFound);
        Assert.AreEqual("event", notFound.Value);
    }

    [TestMethod]
    public async Task LoadEvent_DeletedEvent_ReturnsNotFound()
    {
        _h.AddOrganization(1);
        var evt = _h.AddEvent("Deleted", isDeleted: true);
        await _h.Db.SaveChangesAsync();

        var result = await _h.Controller.LoadEvent(evt.Id);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result);
    }

    /// <summary>An event whose organization row is missing is not returned: the query inner-joins organizations.</summary>
    [TestMethod]
    public async Task LoadEvent_MissingOrganization_ReturnsNotFound()
    {
        var evt = _h.AddEvent("Orphan", organizationId: 42);
        await _h.Db.SaveChangesAsync();

        var result = await _h.Controller.LoadEvent(evt.Id);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task LoadEvent_MapsOrganizationSessionsAndFlags()
    {
        _h.AddOrganization(1, name: "ChampCar", website: "https://champcar.example", controlLogType: "Sheets");
        var evt = _h.AddEvent("Spring Enduro", isPrivate: true, isSimulation: true, trackName: "Sebring");
        await _h.Db.SaveChangesAsync();
        _h.AddSession(1, evt.Id, "Qualifying");
        await _h.Db.SaveChangesAsync();

        var result = await _h.Controller.LoadEvent(evt.Id);

        var loaded = result.Value;
        Assert.IsNotNull(loaded);
        Assert.AreEqual(evt.Id, loaded.EventId);
        Assert.AreEqual("Spring Enduro", loaded.EventName);
        Assert.AreEqual("ChampCar", loaded.OrganizationName);
        Assert.AreEqual("https://champcar.example", loaded.OrganizationWebsite);
        Assert.AreEqual(1, loaded.OrganizationId);
        Assert.AreEqual("Sebring", loaded.TrackName);
        Assert.IsTrue(loaded.HasControlLog);
        Assert.IsTrue(loaded.IsPrivate);
        Assert.IsTrue(loaded.IsSimulation);
        Assert.AreEqual("Qualifying", loaded.Sessions.Single().Name);
    }

    [TestMethod]
    public async Task LoadEvent_HideName_BlanksName()
    {
        _h.AddOrganization(1);
        var evt = _h.AddEvent("Secret", hideName: true);
        await _h.Db.SaveChangesAsync();

        var result = await _h.Controller.LoadEvent(evt.Id);

        Assert.AreEqual(string.Empty, result.Value!.EventName);
        Assert.IsTrue(result.Value.HideName);
    }

    #endregion
}
