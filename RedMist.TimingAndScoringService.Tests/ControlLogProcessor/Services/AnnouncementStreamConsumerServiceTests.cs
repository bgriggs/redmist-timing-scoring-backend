using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.Backend.Shared;
using RedMist.ControlLogProcessor.Services;
using RedMist.ControlLogs.Announcements;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.ControlLogProcessor.Services;

/// <summary>
/// The announcement consumer is the only thing standing between the external timing source's race-control
/// text and the control log the organizers see, so these tests cover what it does with malformed, partial,
/// and empty batches as well as which stream fields it will act on.
/// </summary>
[TestClass]
public class AnnouncementStreamConsumerServiceTests
{
    private const int EventId = 42;

    #region Harness

    private static IConfiguration Config(int eventId = EventId) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["event_id"] = eventId.ToString() }).Build();

    private static AnnouncementStreamConsumerService Service(IAnnouncementControlLogStore store,
        IConnectionMultiplexer? mux = null, int eventId = EventId) =>
        new(NullLoggerFactory.Instance, mux ?? Mock.Of<IConnectionMultiplexer>(), Config(eventId), store);

    private static string BatchJson(params List<Announcement>?[] announcementListsPerPatch)
    {
        var batch = new ExternalPatchBatch
        {
            EventId = EventId,
            SessionId = 1,
            SessionPatches = [.. announcementListsPerPatch.Select(a => new SessionStatePatch { Announcements = a })],
        };
        return JsonSerializer.Serialize(batch);
    }

    private static Announcement Ann(string text, string ts = "2026-06-05T21:00:00Z") =>
        new() { Timestamp = DateTime.Parse(ts).ToUniversalTime(), Text = text, Priority = "Normal" };

    #endregion

    #region Batch handling

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("null")]
    [DataRow("{ this is not json")]
    [DataRow("[1,2,3]")]
    public void ProcessExternalBatch_UnusablePayload_LeavesTheStoreUntouched(string payload)
    {
        var store = new Mock<IAnnouncementControlLogStore>(MockBehavior.Strict);

        using var service = Service(store.Object);
        service.ProcessExternalBatch(payload);

        store.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ProcessExternalBatch_BatchWithNoPatches_LeavesTheStoreUntouched()
    {
        var store = new Mock<IAnnouncementControlLogStore>(MockBehavior.Strict);

        using var service = Service(store.Object);
        service.ProcessExternalBatch(BatchJson());

        store.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ProcessExternalBatch_PatchesThatCarryNoAnnouncements_LeaveTheStoreUntouched()
    {
        // Most session patches are about flags/laps and say nothing about announcements; those must not be
        // read as "the announcement list is now empty".
        var store = new Mock<IAnnouncementControlLogStore>(MockBehavior.Strict);

        using var service = Service(store.Object);
        service.ProcessExternalBatch(BatchJson(null, null));

        store.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void ProcessExternalBatch_UsesTheLastAnnouncementListInTheBatch()
    {
        // The source republishes the whole list on every change, so within one batch the newest list wins
        // outright rather than being merged with the earlier ones.
        var store = new AnnouncementControlLogStore();
        var payload = BatchJson(
            [Ann("CODE 60", "2026-06-05T21:00:00Z")],
            null,
            [Ann("CODE 60", "2026-06-05T21:00:00Z"), Ann("GREEN FLAG", "2026-06-05T21:05:00Z")]);

        using var service = Service(store);
        service.ProcessExternalBatch(payload);

        Assert.AreEqual(2, store.Get().Count);
        CollectionAssert.AreEqual(new[] { "CODE 60", "GREEN FLAG" }, store.Get().Select(e => e.Note).ToList());
    }

    [TestMethod]
    public void ProcessExternalBatch_AnEmptyAnnouncementListClearsTheStore()
    {
        // An explicit empty list is a real state ("race control retracted everything") and is applied as
        // such, unlike a patch that simply omits the field.
        var store = new AnnouncementControlLogStore();
        store.Set([new ControlLogEntry { OrderId = 1, Note = "CODE 60" }]);

        using var service = Service(store);
        service.ProcessExternalBatch(BatchJson(new List<Announcement>()));

        Assert.AreEqual(0, store.Get().Count);
    }

    [TestMethod]
    public void ProcessExternalBatch_ExpandsAMultiCarPenaltyIntoOneEntryPerCar()
    {
        var store = new AnnouncementControlLogStore();

        using var service = Service(store);
        service.ProcessExternalBatch(BatchJson([Ann("Car 14, 392: Penalty - Code 60 Violation - 1 Lap Penalty")]));

        var entries = store.Get();
        CollectionAssert.AreEqual(new[] { "14", "392" }, entries.Select(e => e.Car1).ToList());
        Assert.IsTrue(entries.All(e => e.Status == "Penalty"));
        Assert.IsTrue(entries.All(e => e.OrderId == 1), "both entries share the announcement's chronological position");
    }

    #endregion

    #region Stream loop

    [TestMethod]
    public async Task ExecuteAsync_OnlyExternalPatchFieldsAreParsed_AndTheEntryIsAcknowledged()
    {
        var store = new AnnouncementControlLogStore();
        var streamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, EventId);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var db = new Mock<IDatabase>();
        db.Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        db.Setup(d => d.StreamGroupInfoAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);
        db.Setup(d => d.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);

        var entry = new StreamEntry("1-0",
        [
            // Another producer's field on the same stream - it must be ignored, not fed to the parser.
            new NameValueEntry($"rmonitor-{EventId}-1", "{}"),
            new NameValueEntry($"{Consts.EXTERNAL_PATCH_TYPE}-{EventId}-1", BatchJson([Ann("CAR 14 BEHIND THE WALL")])),
            // Too few segments to be a typed payload.
            new NameValueEntry("external", "{}"),
        ]);

        var reads = new int[1];
        db.Setup(d => d.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => Interlocked.Increment(ref reads[0]) == 1 ? new[] { entry } : Array.Empty<StreamEntry>());

        db.Setup(d => d.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L)
            .Callback(() => acknowledged.TrySetResult());

        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);

        using var service = Service(store, mux.Object);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.AreEqual("14", store.Get().Single().Car1);
        db.Verify(d => d.StreamAcknowledgeAsync(streamKey, It.IsAny<RedisValue>(), "1-0", It.IsAny<CommandFlags>()), Times.Once);
        db.Verify(d => d.StreamCreateConsumerGroupAsync(streamKey, It.IsAny<RedisValue>(),
            StreamPosition.NewMessages, true, It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_EventIdNotConfigured_NeverConnectsToRedis()
    {
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        using var service = Service(new AnnouncementControlLogStore(), mux.Object, eventId: 0);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        mux.VerifyNoOtherCalls();
    }

    #endregion
}
