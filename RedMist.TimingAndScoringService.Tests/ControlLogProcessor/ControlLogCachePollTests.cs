using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Text.RegularExpressions;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;

namespace RedMist.TimingAndScoringService.Tests.ControlLogProcessor;

/// <summary>
/// Poll/diff behavior of <see cref="ControlLogCache.RequestControlLogChangesAsync"/> - the loop that
/// re-reads the organization's control log every minute and reports which cars changed so only those get
/// pushed to clients. Exercised against an in-memory database and a scripted <see cref="IControlLog"/>.
/// </summary>
[TestClass]
public class ControlLogCachePollTests
{
    private const int EventId = 42;
    private const int OrgId = 7;
    private const string LogParams = "sheet-id;Tab 1";

    #region Harness

    /// <summary>An <see cref="IControlLog"/> whose result is scripted per poll.</summary>
    private sealed class ScriptedControlLog(params Func<(bool success, IEnumerable<ControlLogEntry> logs)>[] polls) : IControlLog
    {
        private int pollIndex;

        public List<string> RequestedParameters { get; } = [];

        public string Type => "scripted";
        public Regex WarningPattern { get; } = new(@".*Warning.*", RegexOptions.IgnoreCase);
        public Regex LapPenaltyPattern { get; } = new(@"(\d+)\s+(Lap|Laps)", RegexOptions.IgnoreCase);
        public Regex BlackFlagPattern { get; } = new(@".*Drive Through Penalty.*", RegexOptions.IgnoreCase);

        public Task<(bool success, IEnumerable<ControlLogEntry> logs)> LoadControlLogAsync(string parameter, CancellationToken stoppingToken = default)
        {
            RequestedParameters.Add(parameter);
            var poll = polls[Math.Min(pollIndex++, polls.Length - 1)];
            return Task.FromResult(poll());
        }

        public void Dispose() { }
    }

    private static Func<(bool, IEnumerable<ControlLogEntry>)> Poll(params ControlLogEntry[] entries) => () => (true, entries);

    private static ControlLogEntry Entry(int orderId, string car1, string note = "", string car2 = "",
        string penaltyAction = "", string ts = "2026-06-05T21:00:00Z") => new()
        {
            OrderId = orderId,
            Car1 = car1,
            Car2 = car2,
            Note = note,
            PenaltyAction = penaltyAction,
            Timestamp = DateTime.Parse(ts).ToUniversalTime(),
        };

    private static IDbContextFactory<TsContext> Db(string controlLogType = "scripted", bool withEvent = true, bool withOrg = true)
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var factory = new TestDbContextFactory(options);
        using var db = factory.CreateDbContext();
        if (withEvent)
        {
            db.Events.Add(new ConfigEvent { Id = EventId, OrganizationId = OrgId, Name = "Test Event" });
        }
        if (withOrg)
        {
            db.Organizations.Add(new Organization
            {
                Id = OrgId,
                Name = "Test Org",
                ShortName = "TO",
                ClientId = "test",
                ControlLogType = controlLogType,
                ControlLogParams = LogParams,
            });
        }
        db.SaveChanges();
        return factory;
    }

    private static ControlLogCache Cache(IDbContextFactory<TsContext> db, IControlLog controlLog)
    {
        var factory = new Mock<IControlLogFactory>();
        factory.Setup(f => f.CreateControlLog(It.IsAny<string>())).Returns(controlLog);
        return new ControlLogCache(EventId, NullLoggerFactory.Instance, db, factory.Object);
    }

    #endregion

    #region Change detection

    [TestMethod]
    public async Task FirstPoll_ReportsEveryCarAndRequestsTheOrganizationsConfiguredLog()
    {
        var log = new ScriptedControlLog(Poll(Entry(1, "15"), Entry(2, "23")));
        using var cache = Cache(Db(), log);

        var changes = await cache.RequestControlLogChangesAsync();

        CollectionAssert.AreEquivalent(new[] { "15", "23" }, changes);
        Assert.AreEqual(LogParams, log.RequestedParameters.Single(), "the org's ControlLogParams drive the fetch");
    }

    [TestMethod]
    public async Task SecondPollWithEquivalentEntries_ReportsNothingChanged()
    {
        // Fresh objects with identical field values each poll - the diff must compare by value.
        var log = new ScriptedControlLog(
            Poll(Entry(1, "15", "spun"), Entry(2, "23", "contact")),
            Poll(Entry(1, "15", "spun"), Entry(2, "23", "contact")));
        using var cache = Cache(Db(), log);

        await cache.RequestControlLogChangesAsync();
        var changes = await cache.RequestControlLogChangesAsync();

        Assert.AreEqual(0, changes.Count);
    }

    [TestMethod]
    public async Task EditedEntry_ReportsOnlyTheCarThatChanged()
    {
        var log = new ScriptedControlLog(
            Poll(Entry(1, "15", "spun"), Entry(2, "23", "contact")),
            Poll(Entry(1, "15", "spun - reviewed"), Entry(2, "23", "contact")));
        using var cache = Cache(Db(), log);

        await cache.RequestControlLogChangesAsync();
        var changes = await cache.RequestControlLogChangesAsync();

        CollectionAssert.AreEqual(new[] { "15" }, changes);
    }

    [TestMethod]
    public async Task AdditionalEntryForACar_ReportsThatCar()
    {
        var log = new ScriptedControlLog(
            Poll(Entry(1, "15", "spun")),
            Poll(Entry(1, "15", "spun"), Entry(2, "15", "again")));
        using var cache = Cache(Db(), log);

        await cache.RequestControlLogChangesAsync();
        var changes = await cache.RequestControlLogChangesAsync();

        CollectionAssert.AreEqual(new[] { "15" }, changes);
    }

    [TestMethod]
    public async Task RowsShiftedToNewOrderIds_ReportsTheCarEvenThoughTheCountIsUnchanged()
    {
        // Inserting a row above shifts every OrderId below it; the entry text is identical but the
        // client-side identity is not, so the car has to be republished.
        var log = new ScriptedControlLog(
            Poll(Entry(4, "15", "spun")),
            Poll(Entry(5, "15", "spun")));
        using var cache = Cache(Db(), log);

        await cache.RequestControlLogChangesAsync();
        var changes = await cache.RequestControlLogChangesAsync();

        CollectionAssert.AreEqual(new[] { "15" }, changes);
    }

    [TestMethod]
    public async Task CarThatDropsOutOfTheLog_IsReportedSoItsClientsAreCleared()
    {
        var log = new ScriptedControlLog(
            Poll(Entry(1, "15"), Entry(2, "23")),
            Poll(Entry(1, "15")));
        using var cache = Cache(Db(), log);

        await cache.RequestControlLogChangesAsync();
        var changes = await cache.RequestControlLogChangesAsync();

        CollectionAssert.AreEqual(new[] { "23" }, changes);
    }

    [TestMethod]
    public async Task TwoCarIncident_ReportsBothCarsAndFilesTheEntryUnderEach()
    {
        var log = new ScriptedControlLog(Poll(Entry(1, "15", car2: "23")));
        using var cache = Cache(Db(), log);

        var changes = await cache.RequestControlLogChangesAsync();
        var entries = await cache.GetCarControlEntriesAsync();

        CollectionAssert.AreEquivalent(new[] { "15", "23" }, changes);
        Assert.AreEqual(1, entries["15"].Count);
        Assert.AreEqual(1, entries["23"].Count);
    }

    [TestMethod]
    public async Task CarBucketKeysAreLowerCased()
    {
        var log = new ScriptedControlLog(Poll(Entry(1, "A7")));
        using var cache = Cache(Db(), log);

        var changes = await cache.RequestControlLogChangesAsync();
        var entries = await cache.GetCarControlEntriesAsync();

        CollectionAssert.AreEqual(new[] { "a7" }, changes);
        Assert.IsTrue(entries.ContainsKey("a7"));
    }

    #endregion

    #region Failure handling

    [TestMethod]
    public async Task PollReportingFailure_StillReplacesTheCacheAndRepublishesEveryCar()
    {
        // BUG (pinned, not fixed): the `success` flag from LoadControlLogAsync is discarded, so a transient
        // Google Sheets outage - which returns (false, []) - empties the cached log and reports every car
        // as changed instead of holding the last good snapshot.
        var log = new ScriptedControlLog(
            Poll(Entry(1, "15"), Entry(2, "23")),
            () => (false, []));
        using var cache = Cache(Db(), log);

        await cache.RequestControlLogChangesAsync();
        var changes = await cache.RequestControlLogChangesAsync();

        CollectionAssert.AreEquivalent(new[] { "15", "23" }, changes);
        Assert.AreEqual(0, (await cache.GetCarControlEntriesAsync()).Count, "the cached log was discarded");
    }

    [TestMethod]
    public async Task PollThatThrows_IsSwallowedAndLeavesTheCachedEntriesInPlace()
    {
        var log = new ScriptedControlLog(
            Poll(Entry(1, "15", penaltyAction: "Warning")),
            () => throw new HttpRequestException("sheets unreachable"));
        using var cache = Cache(Db(), log);

        await cache.RequestControlLogChangesAsync();
        var changes = await cache.RequestControlLogChangesAsync();

        Assert.AreEqual(0, changes.Count);
        Assert.AreEqual(1, (await cache.GetCarControlEntriesAsync())["15"].Count, "entries survive a failed poll");
        // ...but the penalty tallies are cleared up front and never rebuilt, so they briefly disagree with
        // the entries that are still being served.
        Assert.AreEqual(0, (await cache.GetPenaltiesAsync()).Count);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "event row exists but its organization does not")]
    [DataRow(false, DisplayName = "no event row at all")]
    public async Task NoOrganizationForTheEvent_ReportsNoChangesAndNeverBuildsAControlLog(bool eventExists)
    {
        var factory = new Mock<IControlLogFactory>(MockBehavior.Strict);
        using var cache = new ControlLogCache(EventId, NullLoggerFactory.Instance, Db(withEvent: eventExists, withOrg: false), factory.Object);

        var changes = await cache.RequestControlLogChangesAsync();

        Assert.AreEqual(0, changes.Count);
        factory.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task OrganizationWithNoControlLogConfigured_ReportsNoChanges()
    {
        var factory = new Mock<IControlLogFactory>(MockBehavior.Strict);
        using var cache = new ControlLogCache(EventId, NullLoggerFactory.Instance, Db(controlLogType: string.Empty), factory.Object);

        var changes = await cache.RequestControlLogChangesAsync();

        Assert.AreEqual(0, changes.Count);
        factory.VerifyNoOtherCalls();
    }

    #endregion

    #region Published views

    [TestMethod]
    public async Task PenaltyTalliesAreRebuiltFromTheLatestPoll()
    {
        var log = new ScriptedControlLog(
            Poll(Entry(1, "15", penaltyAction: "Warning"), Entry(2, "23", penaltyAction: "2 Laps")),
            Poll(Entry(1, "15", penaltyAction: "Warning")));
        using var cache = Cache(Db(), log);

        await cache.RequestControlLogChangesAsync();
        var first = await cache.GetPenaltiesAsync();
        await cache.RequestControlLogChangesAsync();
        var second = await cache.GetPenaltiesAsync();

        Assert.AreEqual(1, first["15"].Warnings);
        Assert.AreEqual(2, first["23"].Laps);
        Assert.IsFalse(second.ContainsKey("23"), "car 23 left the log, so its penalties go with it");
        Assert.AreEqual(1, second["15"].Warnings);
    }

    [TestMethod]
    public async Task GetCarControlEntriesAsync_MatchesRequestedCarNumbersCaseInsensitively()
    {
        var log = new ScriptedControlLog(Poll(Entry(1, "a7")));
        using var cache = Cache(Db(), log);
        await cache.RequestControlLogChangesAsync();

        var entries = await cache.GetCarControlEntriesAsync(["A7", "99"]);

        Assert.AreEqual(1, entries.Count);
        Assert.IsTrue(entries.ContainsKey("A7"), "the caller's spelling is echoed back");
        Assert.AreEqual(1, entries["A7"].Count);
    }

    [TestMethod]
    public async Task GetCarControlEntriesAsync_ReturnsCopiesThatCallersCannotUseToMutateTheCache()
    {
        var log = new ScriptedControlLog(Poll(Entry(1, "15"), Entry(2, "15")));
        using var cache = Cache(Db(), log);
        await cache.RequestControlLogChangesAsync();

        (await cache.GetCarControlEntriesAsync(["15"]))["15"].Clear();
        (await cache.GetCarControlEntriesAsync())["15"].Clear();

        Assert.AreEqual(2, (await cache.GetCarControlEntriesAsync(["15"]))["15"].Count);
    }

    [TestMethod]
    public async Task GetControlEntriesAsync_ReturnsTheWholeLogNewestFirstWithTwoCarIncidentsListedOnce()
    {
        var log = new ScriptedControlLog(Poll(
            Entry(1, string.Empty, note: "CODE 60", ts: "2026-06-05T21:00:00Z"),
            Entry(2, "15", ts: "2026-06-05T21:30:00Z"),
            Entry(3, "5", car2: "9", ts: "2026-06-05T22:00:00Z")));
        using var cache = Cache(Db(), log);
        await cache.RequestControlLogChangesAsync();

        var full = await cache.GetControlEntriesAsync();

        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, full.Select(e => e.OrderId).ToList());
    }

    #endregion
}
