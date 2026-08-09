using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Services;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.PipelineBlocks;
using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.EventStatus.RMonitor;
using RedMist.EventProcessor.Tests.Utilities;

namespace RedMist.EventProcessor.Tests.EventStatus;

[TestClass]
public class SessionMonitorTests
{
    private readonly DebugLoggerFactory lf = new();


    [TestMethod]
    public async Task Session_End_CarsFinishing_Test()
    {
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var sessionContext = CreateSessionContext(1);

        // Create the RMonitor processor to update session state
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockLogger = new Mock<ILogger>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        var mockHubContext = new Mock<IHubContext<StatusHub>>();
        var resetProcessor = new ResetProcessor(sessionContext, mockHubContext.Object, mockLoggerFactory.Object);
        var startingPositionProcessor = new StartingPositionProcessor(sessionContext, mockLoggerFactory.Object, dbMock.Object);
        var rmonitorProcessor = new RMonitorDataProcessor(mockLoggerFactory.Object, sessionContext, resetProcessor, startingPositionProcessor, new Mock<IMediator>().Object);

        // Create the debug session monitor with the SAME session context
        var sessionMonitor = new DebugSessionMonitor(1, dbMock.Object, sessionContext);

        // Initialize the session monitor with a session
        await sessionMonitor.ProcessAsync(36, CancellationToken.None);

        var dataReader = new TestDataReader("event-finish-with-cars-data.log");
        var data = dataReader.GetData();

        int finalCount = 0;
        sessionMonitor.FinalizedSession += () =>
        {
            finalCount++;
        };

        // Store previous state for comparison
        TimingCommon.Models.SessionState? previousState = null;

        int count = 0;
        foreach (var cmd in data)
        {
            count++;

            // Process RMonitor commands to update session state
            var timingMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, cmd, 36, DateTime.UtcNow);
            await rmonitorProcessor.ProcessAsync(timingMessage, sessionContext);

            if (count % 5 == 0)
            {
                // Simulate session monitoring checking for finalized sessions
                await TriggerSessionMonitoring(sessionMonitor, sessionContext, previousState);

                // Capture current state for next comparison
                using (await sessionContext.SessionStateLock.AcquireReadLockAsync(sessionContext.CancellationToken))
                {
                    previousState = CreateSimpleClone(sessionContext.SessionState);
                }
            }
        }

        // Final check for session finalization
        await TriggerSessionMonitoring(sessionMonitor, sessionContext, previousState);

        Assert.IsGreaterThan(0, finalCount, $"Expected at least one session to be finalized, but got {finalCount}. " +
                                      $"Current flag: {sessionContext.SessionState.CurrentFlag}, " +
                                      $"Cars: {sessionContext.SessionState.CarPositions.Count}");
    }

    [TestMethod]
    public async Task Session_End_EventStopping_Test()
    {
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var sessionContext = CreateSessionContext(1);

        // Create the RMonitor processor to update session state
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockLogger = new Mock<ILogger>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        var mockHubContext = new Mock<IHubContext<StatusHub>>();
        var resetProcessor = new ResetProcessor(sessionContext, mockHubContext.Object, mockLoggerFactory.Object);
        var startingPositionProcessor = new StartingPositionProcessor(sessionContext, mockLoggerFactory.Object, dbMock.Object);
        var rmonitorProcessor = new RMonitorDataProcessor(mockLoggerFactory.Object, sessionContext, resetProcessor, startingPositionProcessor, new Mock<IMediator>().Object);

        // Create the debug session monitor with the SAME session context
        var sessionMonitor = new DebugSessionMonitor(1, dbMock.Object, sessionContext);

        // Initialize the session monitor with a session
        await sessionMonitor.ProcessAsync(36, CancellationToken.None);

        var dataReader = new TestDataReader("event-finish-with-stopped.log");
        var data = dataReader.GetData();

        int finalCount = 0;
        sessionMonitor.FinalizedSession += () =>
        {
            finalCount++;
        };

        // Store previous state for comparison
        RedMist.TimingCommon.Models.SessionState? previousState = null;

        int count = 0;
        foreach (var cmd in data)
        {
            count++;

            // Process RMonitor commands to update session state
            var timingMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, cmd, 36, DateTime.UtcNow);
            await rmonitorProcessor.ProcessAsync(timingMessage, sessionContext);

            if (count % 5 == 0)
            {
                // Simulate session monitoring checking for finalized sessions
                await TriggerSessionMonitoring(sessionMonitor, sessionContext, previousState);

                // Capture current state for next comparison
                using (await sessionContext.SessionStateLock.AcquireReadLockAsync(sessionContext.CancellationToken))
                {
                    previousState = CreateSimpleClone(sessionContext.SessionState);
                }
            }
        }

        // Final checks for session finalization
        await TriggerSessionMonitoring(sessionMonitor, sessionContext, previousState);
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(sessionContext.CancellationToken))
        {
            var finalState = CreateSimpleClone(sessionContext.SessionState);
            await TriggerSessionMonitoring(sessionMonitor, sessionContext, finalState);
        }

        Assert.IsGreaterThan(0, finalCount, $"Expected at least one session to be finalized, but got {finalCount}. " +
                                      $"Current flag: {sessionContext.SessionState.CurrentFlag}, " +
                                      $"Cars: {sessionContext.SessionState.CarPositions.Count}");
    }

    [TestMethod]
    public async Task Session_End_Reset_Test()
    {
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var sessionContext = CreateSessionContext(1);

        // Create the RMonitor processor to update session state
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockLogger = new Mock<ILogger>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        var mockHubContext = new Mock<IHubContext<StatusHub>>();
        var resetProcessor = new ResetProcessor(sessionContext, mockHubContext.Object, mockLoggerFactory.Object);
        var startingPositionProcessor = new StartingPositionProcessor(sessionContext, mockLoggerFactory.Object, dbMock.Object);
        var rmonitorProcessor = new RMonitorDataProcessor(mockLoggerFactory.Object, sessionContext, resetProcessor, startingPositionProcessor, new Mock<IMediator>().Object);

        // Create the debug session monitor with the SAME session context
        var sessionMonitor = new DebugSessionMonitor(1, dbMock.Object, sessionContext);

        // Initialize the session monitor with a session
        await sessionMonitor.ProcessAsync(36, CancellationToken.None);

        var dataReader = new TestDataReader("event-finish-with-reset.log");
        var data = dataReader.GetData();

        int finalCount = 0;
        sessionMonitor.FinalizedSession += () =>
        {
            finalCount++;
        };

        // Store previous state for comparison
        RedMist.TimingCommon.Models.SessionState? previousState = null;

        int count = 0;
        foreach (var cmd in data)
        {
            count++;

            // Process RMonitor commands to update session state
            var timingMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, cmd, 36, DateTime.UtcNow);
            await rmonitorProcessor.ProcessAsync(timingMessage, sessionContext);

            if (count % 5 == 0)
            {
                // Simulate session monitoring checking for finalized sessions
                await TriggerSessionMonitoring(sessionMonitor, sessionContext, previousState);

                // Capture current state for next comparison
                using (await sessionContext.SessionStateLock.AcquireReadLockAsync(sessionContext.CancellationToken))
                {
                    previousState = CreateSimpleClone(sessionContext.SessionState);
                }
            }
        }

        // Final checks for session finalization
        await TriggerSessionMonitoring(sessionMonitor, sessionContext, previousState);
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(sessionContext.CancellationToken))
        {
            var finalState = CreateSimpleClone(sessionContext.SessionState);
            await TriggerSessionMonitoring(sessionMonitor, sessionContext, finalState);
        }

        Assert.IsGreaterThan(0, finalCount, $"Expected at least one session to be finalized, but got {finalCount}. " +
                                      $"Current flag: {sessionContext.SessionState.CurrentFlag}, " +
                                      $"Cars: {sessionContext.SessionState.CarPositions.Count}");
    }

    /// <summary>
    /// Saving the results is several database round trips and a synchronous Redis read. The finish
    /// check runs it with the session-state lock released, so the processing pipeline is not stopped
    /// for the duration - at the checkered flag, of all moments. Moving that write back inside the
    /// lock is what this is here to catch.
    /// </summary>
    [TestMethod]
    public async Task Finalization_SavesResultsWithTheSessionStateLockReleased()
    {
        var sessionContext = CreateSessionContext(1);
        var sessionMonitor = new DebugSessionMonitor(1, CreateDbContextFactory(), sessionContext);
        await sessionMonitor.ProcessAsync(36, TestContext.CancellationToken);

        bool? writeLockWasFree = null;
        sessionMonitor.OnPersistFinishedSession = _ =>
        {
            // What the pipeline does on every message. It must not be blocked by this write.
            var acquire = sessionContext.SessionStateLock.AcquireWriteLockAsync();
            writeLockWasFree = acquire.Wait(TimeSpan.FromSeconds(2));
            if (writeLockWasFree == true)
                acquire.Result.Dispose();
            return true;
        };

        await RunToFinishAsync(sessionMonitor, sessionContext);

        Assert.IsNotNull(writeLockWasFree, "The session should have finished and had its results saved.");
        Assert.IsTrue(writeLockWasFree, "Results were saved while the session-state lock was still held.");
    }

    /// <summary>
    /// A session ends once, and this is the only thing that ever writes its results, so a database
    /// that happens to be unreachable at the checkered flag must not cost the session outright.
    /// </summary>
    [TestMethod]
    public async Task Finalization_WhenSavingFails_TriesAgainOnTheNextPass()
    {
        var sessionContext = CreateSessionContext(1);
        var sessionMonitor = new DebugSessionMonitor(1, CreateDbContextFactory(), sessionContext);
        await sessionMonitor.ProcessAsync(36, TestContext.CancellationToken);

        var attempts = new List<int>();
        var saveSucceeds = false;
        sessionMonitor.OnPersistFinishedSession = f =>
        {
            attempts.Add(f.SessionId);
            return saveSucceeds;
        };

        await RunToFinishAsync(sessionMonitor, sessionContext);
        Assert.HasCount(1, attempts, "The session should have been written out once, unsuccessfully.");
        CollectionAssert.AreEqual(new[] { 36 }, attempts);

        // The database comes back.
        saveSucceeds = true;
        await sessionMonitor.RunCheckForFinishedAsync(TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { 36, 36 }, attempts, "The failed results should have been retried.");

        // And are not written again once they are in.
        await sessionMonitor.RunCheckForFinishedAsync(TestContext.CancellationToken);
        Assert.HasCount(2, attempts, "Results that saved should not be written again.");
    }

    /// <summary>
    /// Drives the monitor to a finish: green, then checkered with the event clock stopped. The check
    /// needs one pass to take a baseline, one to see the transition, and two more to conclude the
    /// event time is no longer moving.
    /// </summary>
    private async Task RunToFinishAsync(DebugSessionMonitor monitor, SessionContext sessionContext)
    {
        sessionContext.SessionState.CurrentFlag = TimingCommon.Models.Flags.Green;
        sessionContext.SessionState.LocalTimeOfDay = "10:00:00";
        await monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        sessionContext.SessionState.CurrentFlag = TimingCommon.Models.Flags.Checkered;
        for (int i = 0; i < 3; i++)
            await monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);
    }

    public TestContext TestContext { get; set; } = null!;

    private static SessionContext CreateSessionContext(int eventId)
    {
        var configDict = new Dictionary<string, string?>
        {
            { "event_id", eventId.ToString() }
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        var dbContextFactory = CreateDbContextFactory();
        var loggerFactory = new DebugLoggerFactory();
        var mockLapHistoryService = new Mock<ICarLapHistoryService>();

        return new SessionContext(configuration, dbContextFactory, loggerFactory, mockLapHistoryService.Object);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var databaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(databaseName);
        var options = optionsBuilder.Options;
        return new TestDbContextFactory(options);
    }

    /// <summary>
    /// Simulates the session monitoring by directly calling CheckForFinished
    /// </summary>
    private static async Task TriggerSessionMonitoring(DebugSessionMonitor sessionMonitor, SessionContext sessionContext, RedMist.TimingCommon.Models.SessionState? previousState)
    {
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(sessionContext.CancellationToken))
        {
            var currentState = CreateSimpleClone(sessionContext.SessionState);

            if (previousState != null)
            {
                sessionMonitor.CheckForFinished(previousState, currentState);
            }
            else
            {
                // Create a synthetic "previous" state that would trigger finalization
                var syntheticPreviousState = new TimingCommon.Models.SessionState
                {
                    CurrentFlag = TimingCommon.Models.Flags.Green,
                    EventId = currentState.EventId,
                    SessionId = currentState.SessionId,
                    LocalTimeOfDay = "08:00:00"
                };

                sessionMonitor.CheckForFinished(syntheticPreviousState, currentState);
            }
        }
    }

    /// <summary>
    /// Creates a simple clone of SessionState without relying on generated mappers
    /// </summary>
    private static TimingCommon.Models.SessionState CreateSimpleClone(TimingCommon.Models.SessionState source)
    {
        return new TimingCommon.Models.SessionState
        {
            EventId = source.EventId,
            SessionId = source.SessionId,
            SessionName = source.SessionName,
            CurrentFlag = source.CurrentFlag,
            LocalTimeOfDay = source.LocalTimeOfDay,
            LapsToGo = source.LapsToGo,
            TimeToGo = source.TimeToGo,
            RunningRaceTime = source.RunningRaceTime,
            CarPositions = source.CarPositions.ToList(), // Create new list to avoid reference sharing
            EventName = source.EventName,
            IsPracticeQualifying = source.IsPracticeQualifying,
            SessionStartTime = source.SessionStartTime,
            SessionEndTime = source.SessionEndTime,
            LocalTimeZoneOffset = source.LocalTimeZoneOffset,
            IsLive = source.IsLive,
            EventEntries = source.EventEntries.ToList(),
            FlagDurations = source.FlagDurations.ToList(),
            GreenTimeMs = source.GreenTimeMs,
            GreenLaps = source.GreenLaps,
            YellowTimeMs = source.YellowTimeMs,
            YellowLaps = source.YellowLaps,
            NumberOfYellows = source.NumberOfYellows,
            RedTimeMs = source.RedTimeMs,
            AverageRaceSpeed = source.AverageRaceSpeed,
            LeadChanges = source.LeadChanges,
            Sections = source.Sections.ToList(),
            ClassColors = new Dictionary<string, string>(source.ClassColors),
            Announcements = source.Announcements.ToList(),
            LastUpdated = source.LastUpdated
        };
    }
}
