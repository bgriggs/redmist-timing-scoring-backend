using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Threading.Tasks.Dataflow;

namespace RedMist.TimingAndScoringService.Tests.EventStatus;

[TestClass]
public class SessionStateProcessingPipelineTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private Mock<RMonitorDataProcessorV2> _mockRMonitorProcessor = null!;
    private Mock<MultiloopProcessor> _mockMultiloopProcessor = null!;
    private Mock<PitProcessorV2> _mockPitProcessor = null!;
    private SessionState _sessionState = null!;
    private SessionStateProcessingPipeline _pipeline = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        _mockRMonitorProcessor = new Mock<RMonitorDataProcessorV2>(_mockLoggerFactory.Object);
        _mockMultiloopProcessor = new Mock<MultiloopProcessor>(_mockLoggerFactory.Object, It.IsAny<SessionContext>());
        _mockPitProcessor = new Mock<PitProcessorV2>(
            It.IsAny<Microsoft.EntityFrameworkCore.IDbContextFactory<RedMist.Database.TsContext>>(),
            _mockLoggerFactory.Object,
            It.IsAny<SessionContext>());

        _sessionState = new SessionState();
        _pipeline = new SessionStateProcessingPipeline(
            _sessionState,
            _mockLoggerFactory.Object,
            _mockRMonitorProcessor.Object,
            _mockMultiloopProcessor.Object,
            _mockPitProcessor.Object);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_pipeline != null)
        {
            await _pipeline.CompleteAsync();
        }
    }

    [TestMethod]
    public void Constructor_InitializesCorrectly()
    {
        // Assert
        Assert.IsNotNull(_pipeline);
    }

    [TestMethod]
    public void Post_AcceptsTimingMessage()
    {
        // Arrange
        var message = new TimingMessage("rmonitor", "test data", 1, DateTime.UtcNow);

        // Act
        var result = _pipeline.Post(message);

        // Assert
        Assert.IsTrue(result, "Pipeline should accept the timing message");
    }

    [TestMethod]
    public async Task Post_RMonitorMessage_CallsRMonitorProcessor()
    {
        // Arrange
        var message = new TimingMessage("rmonitor", "test data", 1, DateTime.UtcNow);
        var expectedUpdate = new SessionStateUpdate("rmonitor", new List<ISessionStateChange>());
        
        _mockRMonitorProcessor
            .Setup(x => x.Process(It.IsAny<TimingMessage>()))
            .ReturnsAsync(expectedUpdate);

        // Act
        _pipeline.Post(message);
        await Task.Delay(100); // Give time for processing

        // Assert
        _mockRMonitorProcessor.Verify(x => x.Process(It.Is<TimingMessage>(m => 
            m.Type == "rmonitor" && m.Data == "test data")), Times.Once);
    }

    [TestMethod]
    public async Task Post_MultiloopMessage_CallsMultiloopProcessor()
    {
        // Arrange
        var message = new TimingMessage("multiloop", "test data", 1, DateTime.UtcNow);
        var expectedUpdate = new SessionStateUpdate("multiloop", new List<ISessionStateChange>());
        
        _mockMultiloopProcessor
            .Setup(x => x.Process(It.IsAny<TimingMessage>()))
            .ReturnsAsync(expectedUpdate);

        // Act
        _pipeline.Post(message);
        await Task.Delay(100); // Give time for processing

        // Assert
        _mockMultiloopProcessor.Verify(x => x.Process(It.Is<TimingMessage>(m => 
            m.Type == "multiloop" && m.Data == "test data")), Times.Once);
    }

    [TestMethod]
    public async Task Post_X2PassMessage_CallsPitProcessor()
    {
        // Arrange
        var message = new TimingMessage("x2pass", "test data", 1, DateTime.UtcNow);
        var expectedUpdate = new SessionStateUpdate("PitProcessorV2", new List<ISessionStateChange>());
        
        _mockPitProcessor
            .Setup(x => x.Process(It.IsAny<TimingMessage>()))
            .ReturnsAsync(expectedUpdate);

        // Act
        _pipeline.Post(message);
        await Task.Delay(100); // Give time for processing

        // Assert
        _mockPitProcessor.Verify(x => x.Process(It.Is<TimingMessage>(m => 
            m.Type == "x2pass" && m.Data == "test data")), Times.Once);
    }

    [TestMethod]
    public async Task Subscribe_ReceivesStateChanges()
    {
        // Arrange
        var receivedStates = new List<SessionState>();
        var subscriber = new ActionBlock<SessionState>(state => receivedStates.Add(state));
        var subscription = _pipeline.Subscribe(subscriber);

        var mockStateChange = new Mock<ISessionStateChange>();
        mockStateChange.Setup(x => x.ApplyToState(It.IsAny<SessionState>())).ReturnsAsync(true);
        mockStateChange.Setup(x => x.Targets).Returns(new List<string> { "test" });

        var message = new TimingMessage("rmonitor", "test data", 1, DateTime.UtcNow);
        var update = new SessionStateUpdate("rmonitor", new List<ISessionStateChange> { mockStateChange.Object });
        
        _mockRMonitorProcessor
            .Setup(x => x.Process(It.IsAny<TimingMessage>()))
            .ReturnsAsync(update);

        // Act
        _pipeline.Post(message);
        await Task.Delay(200); // Give time for processing and state update

        // Cleanup
        subscription.Dispose();
        subscriber.Complete();
        await subscriber.Completion;

        // Assert
        Assert.IsTrue(receivedStates.Count > 0, "Should have received state change notifications");
    }

    [TestMethod]
    public async Task GetCurrentStateAsync_ReturnsSessionState()
    {
        // Act
        var currentState = await _pipeline.GetCurrentStateAsync();

        // Assert
        Assert.IsNotNull(currentState);
    }

    [TestMethod]
    public async Task CompleteAsync_CompletesAllBlocks()
    {
        // Arrange
        var message = new TimingMessage("rmonitor", "test data", 1, DateTime.UtcNow);
        _pipeline.Post(message);

        // Act & Assert - Should not throw
        await _pipeline.CompleteAsync();
    }

    [TestMethod]
    public async Task ProcessorReturnsNull_DoesNotUpdateState()
    {
        // Arrange
        var receivedStates = new List<SessionState>();
        var subscriber = new ActionBlock<SessionState>(state => receivedStates.Add(state));
        var subscription = _pipeline.Subscribe(subscriber);

        var message = new TimingMessage("rmonitor", "test data", 1, DateTime.UtcNow);
        
        _mockRMonitorProcessor
            .Setup(x => x.Process(It.IsAny<TimingMessage>()))
            .ReturnsAsync((SessionStateUpdate?)null);

        // Act
        _pipeline.Post(message);
        await Task.Delay(200); // Give time for processing

        // Cleanup
        subscription.Dispose();
        subscriber.Complete();
        await subscriber.Completion;

        // Assert
        Assert.AreEqual(0, receivedStates.Count, "Should not have received state change notifications for null updates");
    }
}