using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Services;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.PipelineBlocks;
using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.RMonitor;

[TestClass]
public class MissingCarResetTests
{
    private RMonitorDataProcessor _processor = null!;
    private SessionContext _sessionContext = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private ResetProcessor _resetProcessor = null!;
    private Mock<IHubContext<StatusHub>> _mockHubContext = null!;
    private Mock<IMediator> _mockMediator = null!;
    private FakeTimeProvider _fakeTimeProvider = null!;
    private StartingPositionProcessor _startingPositionProcessor = null!;

    // Incrementing counters to ensure each call produces a state change
    private int _gLapCounter;
    private int _hLapCounter;
    private int _jLapCounter;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockHubContext = new Mock<IHubContext<StatusHub>>();
        _mockMediator = new Mock<IMediator>();
        _fakeTimeProvider = new FakeTimeProvider();
        _gLapCounter = 1;
        _hLapCounter = 1;
        _jLapCounter = 1;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var dbContextFactory = CreateDbContextFactory();
        var mockLapHistoryService = new Mock<ICarLapHistoryService>();
        _sessionContext = new SessionContext(config, dbContextFactory, _mockLoggerFactory.Object, mockLapHistoryService.Object);

        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        _resetProcessor = new ResetProcessor(_sessionContext, _mockHubContext.Object, _mockLoggerFactory.Object);
        _startingPositionProcessor = new StartingPositionProcessor(_sessionContext, _mockLoggerFactory.Object, dbContextFactory);
        _processor = new RMonitorDataProcessor(_mockLoggerFactory.Object, _sessionContext, _resetProcessor, _startingPositionProcessor, _mockMediator.Object, _fakeTimeProvider);
    }

    /// <summary>
    /// Creates a $G message with an incrementing lap counter to ensure each call produces a state change.
    /// </summary>
    private TimingMessage NextGMessage(string regNum = "1234BE")
    {
        return new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            $"$G,1,\"{regNum}\",{_gLapCounter++},\"00:05:30.123\"", 1, DateTime.Now);
    }

    /// <summary>
    /// Creates a $H message with an incrementing lap counter to ensure each call produces a state change.
    /// </summary>
    private TimingMessage NextHMessage(string regNum = "1234BE")
    {
        return new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            $"$H,2,\"{regNum}\",{_hLapCounter++},\"00:02:17.872\"", 1, DateTime.Now);
    }

    /// <summary>
    /// Creates a $J message with an incrementing lap time to ensure each call produces a state change.
    /// </summary>
    private TimingMessage NextJMessage(string regNum = "1234BE")
    {
        return new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            $"$J,\"{regNum}\",\"00:02:{_jLapCounter++:D2}.826\",\"01:42:17.672\"", 1, DateTime.Now);
    }

    #region Warning Log Tests

    [TestMethod]
    public async Task ProcessG_NoMatchingCar_LogsWarning()
    {
        // Arrange - Send $G command without registering a competitor first
        // Act
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Assert
        VerifyLogWarning("$G command for car");
    }

    [TestMethod]
    public async Task ProcessH_NoMatchingCar_LogsWarning()
    {
        // Arrange - Send $H command without registering a competitor first
        // Act
        await _processor.ProcessAsync(NextHMessage(), _sessionContext);

        // Assert
        VerifyLogWarning("$H command for car");
    }

    [TestMethod]
    public async Task ProcessJ_NoMatchingCar_LogsWarning()
    {
        // Arrange - Send $J command without registering a competitor first
        // Act
        await _processor.ProcessAsync(NextJMessage(), _sessionContext);

        // Assert
        VerifyLogWarning("$J command for car");
    }

    [TestMethod]
    public async Task ProcessG_WithMatchingCar_NoWarning()
    {
        // Arrange - Register competitor where regNum == display number so $G lookup succeeds
        var setupMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$A,\"12\",\"12\",52474,\"John\",\"Doe\",\"USA\",1", 1, DateTime.Now);
        await _processor.ProcessAsync(setupMessage, _sessionContext);

        _mockLogger.Invocations.Clear();

        // Act - Send $G with the same regNum as the display number
        await _processor.ProcessAsync(NextGMessage("12"), _sessionContext);

        // Assert - No warning should be logged for car not found
        VerifyNoLogWarning("car not found");
    }

    #endregion

    #region No Reset Before Threshold Tests

    [TestMethod]
    public async Task MissingCar_UnderThreshold_DoesNotSendReset()
    {
        // Act - Send at time 0
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Advance only 5 seconds (under 10s threshold)
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(5));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Assert - No reset should have been sent
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region Initial Reset After 10s Tests

    [TestMethod]
    public async Task MissingCar_ExceedsThreshold_SendsForcedReset()
    {
        // Act - Send first command to start tracking
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Advance past the 10s threshold
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Assert - Reset should have been sent with ForceTimingDataReset
        _mockMediator.Verify(
            m => m.Publish(
                It.Is<RelayResetRequest>(r => r.EventId == 1 && r.ForceTimingDataReset),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task MissingCar_ExceedsThreshold_ViaHCommand_SendsForcedReset()
    {
        // Act
        await _processor.ProcessAsync(NextHMessage(), _sessionContext);
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextHMessage(), _sessionContext);

        // Assert
        _mockMediator.Verify(
            m => m.Publish(
                It.Is<RelayResetRequest>(r => r.ForceTimingDataReset),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task MissingCar_ExceedsThreshold_ViaJCommand_SendsForcedReset()
    {
        // Act
        await _processor.ProcessAsync(NextJMessage(), _sessionContext);
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextJMessage(), _sessionContext);

        // Assert
        _mockMediator.Verify(
            m => m.Publish(
                It.Is<RelayResetRequest>(r => r.ForceTimingDataReset),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Backoff Tests

    [TestMethod]
    public async Task MissingCar_AfterFirstReset_WaitsBeforeSecondReset()
    {
        // Trigger first reset at 11s
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Try again at 30s after first reset (under 60s backoff)
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(30));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Assert - Should still only have 1 reset
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task MissingCar_AfterFirstReset_SendsSecondResetAfter60s()
    {
        // Trigger first reset at 11s
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Advance past the 60s backoff
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(61));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Assert - Should now have 2 resets
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [TestMethod]
    public async Task MissingCar_ExponentialBackoff_DoublesAfterSecondReset()
    {
        // First reset at 11s
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Second reset after 60s backoff
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(61));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Try at 90s after second reset (under 120s doubled backoff)
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(90));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Assert - Should still only have 2 resets (120s backoff not met)
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Advance past 120s total from second reset (need 31 more seconds)
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(31));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        // Assert - Now should have 3 resets
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [TestMethod]
    public async Task MissingCar_ExponentialBackoff_CapsAtOneHour()
    {
        // First reset at 11s
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);
        int resetCount = 1;

        // Backoff sequence: 60, 120, 240, 480, 960, 1920, 3600, 3600...
        var backoffs = new[] { 60, 120, 240, 480, 960, 1920, 3600, 3600 };
        foreach (var backoff in backoffs)
        {
            _fakeTimeProvider.Advance(TimeSpan.FromSeconds(backoff + 1));
            await _processor.ProcessAsync(NextGMessage(), _sessionContext);
            resetCount++;
        }

        // Assert - Should have sent the expected number of resets
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(resetCount));

        // Verify that at the cap, trying before 1 hour doesn't trigger another reset
        _fakeTimeProvider.Advance(TimeSpan.FromMinutes(59));
        await _processor.ProcessAsync(NextGMessage(), _sessionContext);

        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(resetCount));
    }

    #endregion

    #region Reset Tracking Recovery Tests

    [TestMethod]
    public async Task MissingCar_CarFoundAfterTracking_ResetsBackoff()
    {
        // Start tracking missing cars and trigger a reset
        await _processor.ProcessAsync(NextGMessage("MISS1"), _sessionContext);
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextGMessage("MISS1"), _sessionContext);

        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Register competitor where regNum == display number so $G finds the car
        var competitorMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$A,\"12\",\"12\",52474,\"John\",\"Doe\",\"USA\",1", 1, DateTime.Now);
        await _processor.ProcessAsync(competitorMessage, _sessionContext);

        // Send $G for the registered car - should be found and reset tracking
        await _processor.ProcessAsync(NextGMessage("12"), _sessionContext);

        // Clear invocations to track fresh
        _mockMediator.Invocations.Clear();

        // New missing car command with a different car - should start fresh with 10s threshold
        _gLapCounter = 1; // Reset counter for the new regNum tracking
        await _processor.ProcessAsync(NextGMessage("MISS2"), _sessionContext);

        // Advance only 11s (fresh 10s threshold, not 60s backoff)
        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));
        await _processor.ProcessAsync(NextGMessage("MISS2"), _sessionContext);

        // Assert - Should send reset with fresh timing
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task MissingCar_MixedCommands_SuccessfulCarStopsTracking()
    {
        // Register competitor where regNum == display number
        var competitorMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$A,\"12\",\"12\",52474,\"John\",\"Doe\",\"USA\",1", 1, DateTime.Now);
        await _processor.ProcessAsync(competitorMessage, _sessionContext);

        // Start tracking with a missing car $G (different regNum)
        await _processor.ProcessAsync(NextGMessage("9999ZZ"), _sessionContext);

        _fakeTimeProvider.Advance(TimeSpan.FromSeconds(11));

        // Send a successful car update for the registered competitor - should reset tracking
        await _processor.ProcessAsync(NextGMessage("12"), _sessionContext);

        // Now send the missing car command again - should start fresh tracking (under 10s)
        _gLapCounter = 1;
        await _processor.ProcessAsync(NextGMessage("9999ZZ"), _sessionContext);

        // Assert - No reset sent because tracking was reset by the successful command
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region No Change Commands Don't Trigger Warning

    [TestMethod]
    public async Task ProcessG_NoStateChange_NoWarning()
    {
        // Arrange - Send the same $G command twice; second time produces no change (null from ProcessG)
        var message = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$G,1,\"1234BE\",0,\"00:00:00.000\"", 1, DateTime.Now);

        await _processor.ProcessAsync(message, _sessionContext);
        _mockLogger.Invocations.Clear();

        // Second call with same data produces no change from ProcessG, so no warning check occurs
        await _processor.ProcessAsync(message, _sessionContext);

        // Assert - No reset sent (no change means no missing car check)
        _mockMediator.Verify(
            m => m.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region Helper Methods

    private void VerifyLogWarning(string expectedMessage)
    {
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(expectedMessage)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private void VerifyNoLogWarning(string expectedMessage)
    {
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(expectedMessage)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var databaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(databaseName);
        var options = optionsBuilder.Options;
        return new TestDbContextFactory(options);
    }

    #endregion
}
