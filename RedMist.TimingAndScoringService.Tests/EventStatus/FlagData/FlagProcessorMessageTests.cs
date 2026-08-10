using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.FlagData;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.Tests.EventStatus.FlagData;

/// <summary>
/// The pipeline's entry point into flag handling. The flag log is written from here and it is what
/// the whole session summary - green/yellow time, number of yellows - is computed from afterwards,
/// so a message that is dropped here is not recoverable. FlagProcessorTests covers the log itself.
/// </summary>
[TestClass]
public class FlagProcessorMessageTests
{
    private const int EventId = 4;
    private const int SessionId = 11;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Process_AFlagsMessage_PersistsTheFlagsAndPatchesTheSession()
    {
        var (processor, context, dbFactory) = Create();
        var flags = new List<FlagDuration>
        {
            new() { Flag = Flags.Green, StartTime = new DateTime(2026, 4, 26, 10, 0, 0), EndTime = new DateTime(2026, 4, 26, 10, 30, 0) },
            new() { Flag = Flags.Yellow, StartTime = new DateTime(2026, 4, 26, 10, 30, 0) },
        };

        var updates = await processor.Process(Message(flags));

        Assert.IsNotNull(updates);
        Assert.HasCount(1, updates.SessionPatches);
        Assert.HasCount(2, updates.SessionPatches[0].FlagDurations!);
        Assert.HasCount(2, context.SessionState.FlagDurations, "The session state has to carry the durations for clients.");

        await using var db = dbFactory.CreateDbContext();
        var logged = db.FlagLog.Where(f => f.EventId == EventId && f.SessionId == SessionId).ToList();
        Assert.HasCount(2, logged);
        Assert.AreEqual(SessionId, processor.SessionId);
    }

    /// <summary>
    /// The relay resends the whole flag list on every change, so most messages carry nothing new.
    /// Patching on those would republish the full list to every client for no reason.
    /// </summary>
    [TestMethod]
    public async Task Process_TheSameFlagsAgain_ProducesNoPatch()
    {
        var (processor, _, _) = Create();
        var flags = new List<FlagDuration>
        {
            new() { Flag = Flags.Green, StartTime = new DateTime(2026, 4, 26, 10, 0, 0) },
        };
        await processor.Process(Message(flags));

        var updates = await processor.Process(Message(flags));

        Assert.IsNull(updates);
    }

    /// <summary>
    /// A flag ending is a change to an entry already in the list rather than a new entry, and it is
    /// what the durations are computed from.
    /// </summary>
    [TestMethod]
    public async Task Process_WhenAFlagGainsAnEndTime_PatchesTheSession()
    {
        var (processor, _, _) = Create();
        var start = new DateTime(2026, 4, 26, 10, 0, 0);
        await processor.Process(Message([new FlagDuration { Flag = Flags.Green, StartTime = start }]));

        var updates = await processor.Process(Message(
            [new FlagDuration { Flag = Flags.Green, StartTime = start, EndTime = start.AddMinutes(30) }]));

        Assert.IsNotNull(updates);
        Assert.AreEqual(start.AddMinutes(30), updates.SessionPatches[0].FlagDurations![0].EndTime);
    }

    [TestMethod]
    public async Task Process_AMessageOfAnotherType_IsIgnored()
    {
        var (processor, _, dbFactory) = Create();

        var updates = await processor.Process(
            new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", SessionId, DateTime.UtcNow));

        Assert.IsNull(updates);
        await using var db = dbFactory.CreateDbContext();
        Assert.IsEmpty(db.FlagLog.ToList());
    }

    /// <summary>
    /// The message path needs the session context to know which session the flags belong to; the
    /// legacy constructor has none, and silently logging them against session zero would put them
    /// in the wrong session's results.
    /// </summary>
    [TestMethod]
    public async Task Process_WithoutASessionContext_Throws()
    {
        var processor = new FlagProcessor(EventId, CreateDbContextFactory(), new DebugLoggerFactory());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => processor.Process(Message([new FlagDuration { Flag = Flags.Green, StartTime = DateTime.UtcNow }])));
    }

    private static TimingMessage Message(List<FlagDuration> flags)
        => new(Backend.Shared.Consts.FLAGS_TYPE, JsonSerializer.Serialize(flags), SessionId, DateTime.UtcNow);

    private static (FlagProcessor Processor, SessionContext Context, IDbContextFactory<TsContext> DbFactory) Create()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();
        var dbFactory = CreateDbContextFactory();
        var loggerFactory = new DebugLoggerFactory();
        var context = new SessionContext(configuration, dbFactory, loggerFactory, new Mock<ICarLapHistoryService>().Object);
        context.SessionState.SessionId = SessionId;
        return (new FlagProcessor(dbFactory, loggerFactory, context), context, dbFactory);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        return new TestDbContextFactory(optionsBuilder.Options);
    }
}
