using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Backend.Shared.Services;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.TimingAndScoringService.Tests.EventStatus;

[TestClass]
public class ConsistencyCheckServiceTests
{
    private readonly Mock<ILoggerFactory> mockLoggerFactory;
    private readonly Mock<ILogger> mockLogger;
    private readonly SessionContext sessionContext;
    private readonly Mock<IMediator> mockMediator;
    private readonly FakeTimeProvider fakeTimeProvider;
    private readonly ConsistencyCheckOptions testOptions;

    public ConsistencyCheckServiceTests()
    {
        mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLogger = new Mock<ILogger>();
        mockMediator = new Mock<IMediator>();
        fakeTimeProvider = new FakeTimeProvider();
        
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(mockLogger.Object);

        // Create a real SessionContext with configuration
        var configDict = new Dictionary<string, string?> { { "event_id", "123" } };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
        sessionContext = new SessionContext(configuration, fakeTimeProvider);

        testOptions = new ConsistencyCheckOptions
        {
            MainLoopInterval = TimeSpan.FromMilliseconds(100),
            ErrorThrottleDelay = TimeSpan.FromMilliseconds(100),
            ConsistencyErrorRecheckInterval = TimeSpan.FromMilliseconds(50),
            MaxConsistencyErrorsBeforeRelayReset = 2,
            MinResetIntervalMinutes = 1.0,
            ForceResetThresholdMinutes = 2.0,
            MinForceReconnectIntervalMinutes = 3.0
        };
    }

    private TestableConsistencyCheckService CreateService(ImmutableList<CarPosition>? cars = null)
    {
        return new TestableConsistencyCheckService(
            mockLoggerFactory.Object,
            sessionContext,
            mockMediator.Object,
            fakeTimeProvider,
            testOptions,
            cars ?? []);
    }

    [TestMethod]
    public async Task PerformConsistencyCheck_WithConsistentCars_DoesNotSendRelayReset()
    {
        // Arrange
        var consistentCars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 2 },
            new CarPosition { Number = "3", OverallPosition = 3 }
        );
        var service = CreateService(consistentCars);

        // Act
        await service.PerformConsistencyCheck(CancellationToken.None);

        // Assert
        mockMediator.Verify(x => x.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsFalse(service.RelayResetCalled);
    }

    [TestMethod]
    public async Task PerformConsistencyCheck_WithInconsistentCars_SendsRelayResetAfterRetries()
    {
        // Arrange
        var inconsistentCars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 3 }, // Gap in positions
            new CarPosition { Number = "3", OverallPosition = 4 }
        );
        var service = CreateService(inconsistentCars);

        // Act
        await service.PerformConsistencyCheck(CancellationToken.None);

        // Assert
        mockMediator.Verify(x => x.Publish(It.Is<RelayResetRequest>(r => 
            r.EventId == sessionContext.EventId && 
            !r.ForceTimingDataReset), It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsTrue(service.RelayResetCalled);
        Assert.AreEqual(testOptions.MaxConsistencyErrorsBeforeRelayReset, service.RetryCount);
    }

    [TestMethod]
    public async Task PerformConsistencyCheck_InconsistencyResolves_StopsRetrying()
    {
        // Arrange
        var inconsistentCars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 3 }
        );
        var consistentCars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 2 }
        );
        var service = CreateService(inconsistentCars);
        service.CarsToReturnAfterFirstCheck = consistentCars;

        // Act
        await service.PerformConsistencyCheck(CancellationToken.None);

        // Assert
        mockMediator.Verify(x => x.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsFalse(service.RelayResetCalled);
        Assert.AreEqual(1, service.RetryCount);
    }

    [TestMethod]
    public async Task SendRelayReset_WithRecentReset_DoesNotForceTimingDataReset()
    {
        // Arrange
        var service = CreateService();
        var baseTime = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        fakeTimeProvider.SetUtcNow(baseTime);

        // Act
        await service.TestSendRelayReset();

        // Assert
        mockMediator.Verify(x => x.Publish(It.Is<RelayResetRequest>(r => 
            !r.ForceTimingDataReset), It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(baseTime, service.LastConsistencyError);
    }

    [TestMethod]
    public async Task SendRelayReset_WithOlderResetInTimeWindow_ForcesTimingDataReset()
    {
        // Arrange
        var service = CreateService();
        var baseTime = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        fakeTimeProvider.SetUtcNow(baseTime);
        
        // Simulate a previous reset 1.5 minutes ago by first resetting, then advancing time forward
        service.ResetInternalState();
        await service.TestSendRelayReset(); // This sets lastConsistencyError to baseTime
        
        // Advance time by 1.5 minutes to simulate the time window scenario
        var advancedTime = baseTime.AddMinutes(1.5);
        fakeTimeProvider.SetUtcNow(advancedTime);

        // Act
        await service.TestSendRelayReset();

        // Assert
        mockMediator.Verify(x => x.Publish(It.Is<RelayResetRequest>(r => 
            r.ForceTimingDataReset), It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(advancedTime, service.LastRelayForceReconnect);
    }

    [TestMethod]
    public void Options_AreConfigurable()
    {
        // Arrange & Act
        var service = CreateService();

        // Assert
        Assert.AreEqual(testOptions.MainLoopInterval, service.Options.MainLoopInterval);
        Assert.AreEqual(testOptions.MaxConsistencyErrorsBeforeRelayReset, service.Options.MaxConsistencyErrorsBeforeRelayReset);
        Assert.AreEqual(testOptions.ConsistencyErrorRecheckInterval, service.Options.ConsistencyErrorRecheckInterval);
    }

    [TestMethod]
    public async Task ResetInternalState_ClearsTimestamps()
    {
        // Arrange
        var service = CreateService();
        fakeTimeProvider.SetUtcNow(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero));
        
        // Act - trigger some state changes
        await service.TestSendRelayReset();
        Assert.AreNotEqual(DateTime.MinValue, service.LastConsistencyError);
        
        // Reset state
        service.ResetInternalState();

        // Assert
        Assert.AreEqual(DateTime.MinValue, service.LastConsistencyError);
        Assert.AreEqual(DateTime.MinValue, service.LastRelayForceReconnect);
    }

    // Test double to override protected methods for easier testing
    private class TestableConsistencyCheckService : ConsistencyCheckService
    {
        private readonly ImmutableList<CarPosition> carsToReturn;
        public ImmutableList<CarPosition>? CarsToReturnAfterFirstCheck { get; set; }
        public bool RelayResetCalled { get; private set; }
        public int RetryCount { get; private set; }
        private int checkCount = 0;

        public TestableConsistencyCheckService(
            ILoggerFactory loggerFactory,
            SessionContext sessionContext,
            IMediator mediator,
            TimeProvider timeProvider,
            ConsistencyCheckOptions options,
            ImmutableList<CarPosition> carsToReturn)
            : base(loggerFactory, sessionContext, mediator, timeProvider, null, options)
        {
            this.carsToReturn = carsToReturn;
        }

        protected override async Task<ImmutableList<CarPosition>> GetCarsAsync(CancellationToken stoppingToken)
        {
            await Task.CompletedTask;
            checkCount++;
            
            // Return different data after first check if specified (for testing resolution scenarios)
            if (checkCount > 1 && CarsToReturnAfterFirstCheck != null)
            {
                RetryCount = checkCount - 1;
                return CarsToReturnAfterFirstCheck;
            }
            
            RetryCount = checkCount - 1;
            return carsToReturn;
        }

        protected override async Task SendRelayReset()
        {
            RelayResetCalled = true;
            await base.SendRelayReset();
        }

        public async Task TestSendRelayReset() => await base.SendRelayReset();
    }
}