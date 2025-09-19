using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor;

[TestClass]
public class RMonitorDataProcessorV2Tests
{
    private RMonitorDataProcessorV2 _processor = null!;
    private SessionContext _sessionContext = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private ResetProcessor _resetProcessor = null!;
    private Mock<IHubContext<StatusHub>> _mockHubContext = null!;
    private StartingPositionProcessor _startingPositionProcessor = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockHubContext = new Mock<IHubContext<StatusHub>>();

        // Setup session context FIRST
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        _sessionContext = new SessionContext(config);

        // Verify that CreateLogger is being called and set up the factory to return our mock
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        _resetProcessor = new ResetProcessor(_sessionContext, _mockHubContext.Object, _mockLoggerFactory.Object);
        _startingPositionProcessor = new StartingPositionProcessor(_sessionContext, _mockLoggerFactory.Object);
        _processor = new RMonitorDataProcessorV2(_mockLoggerFactory.Object, _sessionContext, _resetProcessor, _startingPositionProcessor);
    }

    [TestMethod]
    public async Task ProcessWithImmediateApplication_NonRMonitorMessage_ReturnsNull()
    {
        // Arrange
        var message = new TimingMessage("other", "data", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(message, _sessionContext);

        // Assert
        Assert.IsNull(result); // Non-RMonitor messages should return null
    }

    [TestMethod]
    public async Task ProcessWithImmediateApplication_RMonitorMessage_ReturnsPatchUpdates()
    {
        // Arrange
        var message = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(message, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.SessionPatches.Count > 0 || result.CarPatches.Count > 0);
    }

    #region Heartbeat Tests ($F)

    [TestMethod]
    public async Task ProcessF_ValidHeartbeat_GeneratesSessionPatches()
    {
        // Arrange
        var message = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(message, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.SessionPatches.Count > 0);

        // Verify heartbeat data is applied to session context
        Assert.AreEqual(14, _sessionContext.SessionState.LapsToGo);
        Assert.AreEqual("00:12:45", _sessionContext.SessionState.TimeToGo);
        Assert.AreEqual("13:34:23", _sessionContext.SessionState.LocalTimeOfDay);
        Assert.AreEqual("00:09:47", _sessionContext.SessionState.RunningRaceTime);
    }

    #endregion

    #region Competitor Tests ($A and $COMP)

    [TestMethod]
    public async Task ProcessA_ValidCompetitor_GeneratesSessionAndCarPatches()
    {
        // Arrange
        var competitorMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(competitorMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);

        // The current implementation should generate car patches when competitors are added
        Assert.IsTrue(result.CarPatches.Count > 0, "Expected car patches to be generated for new competitor");

        // Verify competitor data is applied to session context
        Assert.IsTrue(_sessionContext.SessionState.EventEntries.Count > 0);
        Assert.IsTrue(_sessionContext.SessionState.CarPositions.Count > 0);
    }

    [TestMethod]
    public async Task ProcessComp_ValidCompetitor_GeneratesPatches()
    {
        // Arrange
        var competitorMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$COMP,\"1234BE\",\"12X\",5,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(competitorMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);

        // The current implementation should generate car patches when competitors are added
        Assert.IsTrue(result.CarPatches.Count > 0, "Expected car patches to be generated for new competitor");

        // Verify competitor data is applied to session context
        Assert.IsTrue(_sessionContext.SessionState.EventEntries.Count > 0);
        Assert.IsTrue(_sessionContext.SessionState.CarPositions.Count > 0);
    }

    [TestMethod]
    public async Task ProcessA_MultipleCompetitors_GeneratesPatches()
    {
        // Arrange
        var multipleCompetitors = "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5\n" +
                                 "$A,\"5678CD\",\"34Y\",12345,\"Jane\",\"Smith\",\"CAN\",3";
        var message = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, multipleCompetitors, 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(message, _sessionContext);

        // Assert
        Assert.IsNotNull(result);

        // The current implementation should generate car patches when competitors are added
        Assert.IsTrue(result.CarPatches.Count >= 2, $"Expected at least 2 car patches to be generated for new competitors, got {result.CarPatches.Count}");

        // Verify multiple competitors are applied
        Assert.IsTrue(_sessionContext.SessionState.CarPositions.Count >= 2);
    }

    #endregion

    #region Session Information Tests ($B)

    [TestMethod]
    public async Task ProcessB_ValidSessionInfo_GeneratesSessionPatch()
    {
        // Arrange
        var sessionMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "$B,5,\"Friday free practice\"", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(sessionMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.SessionPatches.Count > 0);

        // Verify session data is applied to context
        Assert.AreEqual(5, _sessionContext.SessionState.SessionId);
        Assert.AreEqual("Friday free practice", _sessionContext.SessionState.SessionName);
    }

    [TestMethod]
    public async Task ProcessB_SameSessionReference_DoesNotGenerateSessionPatch()
    {
        // Arrange - Set initial session reference
        _processor.SessionReference = 5;
        var sessionMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "$B,5,\"Friday free practice\"", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(sessionMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count); // Same session reference should not generate patches
        Assert.AreEqual(0, result.CarPatches.Count);
    }

    #endregion

    #region Reset Tests ($I)

    [TestMethod]
    public async Task ProcessI_Reset_ClearsCarPositions()
    {
        // Arrange - First add some car positions
        _sessionContext.SessionState.CarPositions.Add(new CarPosition { Number = "1", DriverName = "Test Driver" });
        _sessionContext.SessionState.CarPositions.Add(new CarPosition { Number = "2", DriverName = "Another Driver" });

        var resetMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "$I,\"16:36:08.000\",\"12 jan 01\"", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(resetMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);

        // The reset should clear car positions - this happens in the ResetProcessor.Process() call
        // which is called asynchronously within ProcessI, so car positions should be cleared
        Assert.AreEqual(0, _sessionContext.SessionState.CarPositions.Count, "Car positions should be cleared after reset");
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    public async Task ProcessWithImmediateApplication_ExceptionInCommand_LogsError()
    {
        // Arrange - Create a malformed command that will cause parsing errors
        var malformedMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "$B,invalid", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(malformedMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count); // Malformed command should not generate patches
        Assert.AreEqual(0, result.CarPatches.Count);
        VerifyLogError("Error processing command");
    }

    [TestMethod]
    public async Task ProcessWithImmediateApplication_UnknownCommand_LogsWarning()
    {
        // Arrange
        var unknownMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "$UNKNOWN,data", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(unknownMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count); // Unknown command should not generate patches
        Assert.AreEqual(0, result.CarPatches.Count);
        VerifyLogWarning("Unknown command");
    }

    [TestMethod]
    public async Task ProcessWithImmediateApplication_EmptyData_HandlesGracefully()
    {
        // Arrange
        var emptyMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, "", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(emptyMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count);
        Assert.AreEqual(0, result.CarPatches.Count);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public async Task ProcessWithImmediateApplication_CompleteRaceScenario_ProcessesCorrectly()
    {
        // Arrange - Simulate a complete race data scenario
        var raceScenario =
            // Session setup
            "$B,1,\"Race 1\"\n" +
            "$E,\"TRACKNAME\",\"Road America\"\n" +
            "$E,\"TRACKLENGTH\",\"4.048\"\n" +

            // Classes
            "$C,1,\"GT1\"\n" +
            "$C,2,\"GT2\"\n" +

            // Competitors
            "$A,\"1234BE\",\"12\",52474,\"John\",\"Doe\",\"USA\",1\n" +
            "$A,\"5678CD\",\"34\",12345,\"Jane\",\"Smith\",\"CAN\",2\n" +

            // Race data
            "$F,50,\"00:45:30\",\"14:30:15\",\"00:15:30\",\"Green \"";

        var message = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, raceScenario, 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(message, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.SessionPatches.Count > 0);
        Assert.IsTrue(result.CarPatches.Count > 0);

        // Verify session data is applied immediately
        Assert.AreEqual(1, _sessionContext.SessionState.SessionId);
        Assert.AreEqual("Race 1", _sessionContext.SessionState.SessionName);
        Assert.AreEqual(50, _sessionContext.SessionState.LapsToGo);
        Assert.AreEqual("00:45:30", _sessionContext.SessionState.TimeToGo);

        // Verify competitors are added
        Assert.IsTrue(_sessionContext.SessionState.CarPositions.Count >= 2);
    }

    [TestMethod]
    public async Task ProcessWithImmediateApplication_CommandOrdering_ProcessesInOrder()
    {
        // Arrange - Test that commands are processed in order
        var orderedCommands =
            "$I,\"16:36:08.000\",\"12 jan 01\"\n" +  // Reset first
            "$A,\"1234BE\",\"12\",52474,\"John\",\"Doe\",\"USA\",1\n" +  // Add competitor
            "$F,50,\"00:45:30\",\"14:30:15\",\"00:15:30\",\"Green \"";  // Heartbeat last

        var message = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, orderedCommands, 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(message, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.SessionPatches.Count > 0); // Session patches from reset, competitor, and heartbeat
        Assert.IsTrue(result.CarPatches.Count > 0); // Car patches from competitor

        // Verify reset happened first (car positions cleared)
        // Then competitor added 
        // Then heartbeat applied
        Assert.IsTrue(_sessionContext.SessionState.CarPositions.Count > 0); // Competitor was added after reset
        Assert.AreEqual(50, _sessionContext.SessionState.LapsToGo); // Heartbeat was applied
    }

    #endregion

    #region Immediate Application Tests

    [TestMethod]
    public async Task ProcessWithImmediateApplication_CarDataImmediatelyAvailable_AfterRaceCommand()
    {
        // Arrange - First add competitor and class information
        var setupMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$C,1,\"GT1\"\n" +
            "$A,\"1234BE\",\"12\",52474,\"John\",\"Doe\",\"USA\",1", 1, DateTime.Now);

        await _processor.ProcessAsync(setupMessage, _sessionContext);

        // Arrange - Now send race data that should see the competitor
        var raceMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$G,1,\"1234BE\",5,\"00:05:30.123\"", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(raceMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);

        // Verify the car position was found and updated (due to immediate competitor application)
        var car = _sessionContext.GetCarByNumber("12");
        Assert.IsNotNull(car);

        // The class should be mapped since we processed the class first
        Assert.AreEqual("GT1", car.Class);
        Assert.AreEqual("John", car.DriverName);
    }

    [TestMethod]
    public async Task ProcessWithImmediateApplication_ResetClearsStateImmediately()
    {
        // Arrange - First add some competitors
        var setupMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$A,\"1234BE\",\"12\",52474,\"John\",\"Doe\",\"USA\",1", 1, DateTime.Now);

        await _processor.ProcessAsync(setupMessage, _sessionContext);
        Assert.IsTrue(_sessionContext.SessionState.CarPositions.Count > 0);

        // Act - Send reset in same message with new competitor
        var resetMessage = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$I,\"16:36:08.000\",\"12 jan 01\"\n" +
            "$A,\"5678CD\",\"34\",12345,\"Jane\",\"Smith\",\"CAN\",2", 1, DateTime.Now);

        var result = await _processor.ProcessAsync(resetMessage, _sessionContext);

        // Assert
        Assert.IsNotNull(result);

        // Verify reset happened first, then competitor was added
        Assert.IsTrue(_sessionContext.SessionState.CarPositions.Count > 0);
        var newCar = _sessionContext.GetCarByNumber("34");
        Assert.IsNotNull(newCar);

        // Based on AddUpdateCompetitor implementation, DriverName is set to FirstName only
        Assert.AreEqual("Jane", newCar.DriverName);

        // The reset clears internal competitor dictionaries, so the old competitor should be gone
        // However, if the old competitor had the same registration number as the new one, it might still exist
        // Let's check if the old car is truly gone by checking there's only one car now
        Assert.AreEqual(1, _sessionContext.SessionState.CarPositions.Count, "Should only have one car after reset and adding new competitor");

        // Verify the remaining car is the new one, not the old one
        var remainingCar = _sessionContext.SessionState.CarPositions.First();
        Assert.AreEqual("34", remainingCar.Number);
        Assert.AreEqual("Jane", remainingCar.DriverName);
    }

    [TestMethod]
    public async Task ProcessWithImmediateApplication_SessionStateImmediatelyUpdated()
    {
        // Arrange
        var message = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$B,5,\"Friday Practice\"\n" +
            "$F,25,\"00:30:00\",\"14:30:15\",\"00:10:00\",\"Yellow\"", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(message, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.SessionPatches.Count >= 2); // Session state update and heartbeat

        // Verify session state was immediately updated and heartbeat could see it
        Assert.AreEqual(5, _sessionContext.SessionState.SessionId);
        Assert.AreEqual("Friday Practice", _sessionContext.SessionState.SessionName);
        Assert.AreEqual(25, _sessionContext.SessionState.LapsToGo);
        Assert.AreEqual("00:30:00", _sessionContext.SessionState.TimeToGo);
    }

    [TestMethod]
    public async Task ProcessWithImmediateApplication_CompetitorClassMappingWorksImmediately()
    {
        // Arrange - Process class and competitor in same message
        var message = new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$C,5,\"Formula 300\"\n" +
            "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5", 1, DateTime.Now);

        // Act
        var result = await _processor.ProcessAsync(message, _sessionContext);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.CarPatches.Count > 0, "Expected car patches to be generated");

        // Verify competitor was created with correct class mapping
        var car = _sessionContext.GetCarByNumber("12X");
        Assert.IsNotNull(car);
        Assert.AreEqual("Formula 300", car.Class);

        var entry = _sessionContext.SessionState.EventEntries.FirstOrDefault(e => e.Number == "12X");
        Assert.IsNotNull(entry);
        Assert.AreEqual("Formula 300", entry.Class);
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

    private void VerifyLogError(string expectedMessage)
    {
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(expectedMessage)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    #endregion
}