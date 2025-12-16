using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.InCarDriverMode;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;

namespace RedMist.EventProcessor.Tests.EventStatus.InCarDriverMode;

[TestClass]
public class DriverModeProcessorTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private SessionContext _sessionContext = null!;
    private Mock<ICarPositionProvider> _mockCarPositionProvider = null!;
    private Mock<ICompetitorMetadataProvider> _mockCompetitorMetadataProvider = null!;
    private Mock<IInCarUpdateSender> _mockUpdateSender = null!;
    private DriverModeProcessor _processor = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockCarPositionProvider = new Mock<ICarPositionProvider>();
        _mockCompetitorMetadataProvider = new Mock<ICompetitorMetadataProvider>();
        _mockUpdateSender = new Mock<IInCarUpdateSender>();

        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        // Create a real SessionContext instead of mocking it
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var dbContextFactory = CreateDbContextFactory();
        _sessionContext = new SessionContext(configuration, dbContextFactory, new FakeTimeProvider());

        _processor = new DriverModeProcessor(
            loggerFactory: _mockLoggerFactory.Object,
            sessionContext: _sessionContext,
            carPositionProvider: _mockCarPositionProvider.Object,
            competitorMetadataProvider: _mockCompetitorMetadataProvider.Object,
            updateSender: _mockUpdateSender.Object
            );
        }

        private static IDbContextFactory<TsContext> CreateDbContextFactory()
        {
            var databaseName = $"TestDatabase_{Guid.NewGuid()}";
            var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
            optionsBuilder.UseInMemoryDatabase(databaseName);
            var options = optionsBuilder.Options;
            return new TestDbContextFactory(options);
        }

    [TestMethod]
    public void GetCarAhead_WithValidClassPosition_ReturnsCorrectCar()
    {
        // Arrange
        var driver = new CarPosition { ClassPosition = 3, Class = "GT1" };
        var carPositions = new List<CarPosition>
        {
            new() { ClassPosition = 1, Class = "GT1", Number = "1" },
            new() { ClassPosition = 2, Class = "GT1", Number = "2" },
            new() { ClassPosition = 3, Class = "GT1", Number = "3" },
            new() { ClassPosition = 1, Class = "GT2", Number = "4" }
        }.AsReadOnly();

        // Act
        var result = _processor.GetCarAhead(driver, carPositions);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("2", result.Number);
        Assert.AreEqual(2, result.ClassPosition);
        Assert.AreEqual("GT1", result.Class);
    }

    [TestMethod]
    public void GetCarAhead_WithFirstPosition_ReturnsNull()
    {
        // Arrange
        var driver = new CarPosition { ClassPosition = 1, Class = "GT1" };
        var carPositions = new List<CarPosition>
        {
            new() { ClassPosition = 1, Class = "GT1", Number = "1" },
            new() { ClassPosition = 2, Class = "GT1", Number = "2" }
        }.AsReadOnly();

        // Act
        var result = _processor.GetCarAhead(driver, carPositions);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetCarBehind_WithValidClassPosition_ReturnsCorrectCar()
    {
        // Arrange
        var driver = new CarPosition { ClassPosition = 2, Class = "GT1" };
        var carPositions = new List<CarPosition>
        {
            new() { ClassPosition = 1, Class = "GT1", Number = "1" },
            new() { ClassPosition = 2, Class = "GT1", Number = "2" },
            new() { ClassPosition = 3, Class = "GT1", Number = "3" }
        }.AsReadOnly();

        // Act
        var result = _processor.GetCarBehind(driver, carPositions);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("3", result.Number);
        Assert.AreEqual(3, result.ClassPosition);
        Assert.AreEqual("GT1", result.Class);
    }

    [TestMethod]
    public void GetCarBehind_WithLastPosition_ReturnsNull()
    {
        // Arrange
        var driver = new CarPosition { ClassPosition = 3, Class = "GT1" };
        var carPositions = new List<CarPosition>
        {
            new() { ClassPosition = 1, Class = "GT1", Number = "1" },
            new() { ClassPosition = 2, Class = "GT1", Number = "2" },
            new() { ClassPosition = 3, Class = "GT1", Number = "3" }
        }.AsReadOnly();

        // Act
        var result = _processor.GetCarBehind(driver, carPositions);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetCarAheadOutOfClass_WithValidOverallPosition_ReturnsCorrectCar()
    {
        // Arrange
        var driver = new CarPosition { OverallPosition = 3, Class = "GT1" };
        var carPositions = new List<CarPosition>
        {
            new() { OverallPosition = 1, Class = "GT2", Number = "1" },
            new() { OverallPosition = 2, Class = "GT1", Number = "2" },
            new() { OverallPosition = 3, Class = "GT1", Number = "3" },
            new() { OverallPosition = 4, Class = "GT2", Number = "4" }
        }.AsReadOnly();

        // Act
        var result = _processor.GetCarAheadOutOfClass(driver, carPositions);

        // Assert
        Assert.IsNull(result); // Car ahead is same class, so should return null
    }

    [TestMethod]
    public void GetCarAheadOutOfClass_WithDifferentClassAhead_ReturnsCorrectCar()
    {
        // Arrange
        var driver = new CarPosition { OverallPosition = 3, Class = "GT1" };
        var carPositions = new List<CarPosition>
        {
            new() { OverallPosition = 1, Class = "GT2", Number = "1" },
            new() { OverallPosition = 2, Class = "GT2", Number = "2" },
            new() { OverallPosition = 3, Class = "GT1", Number = "3" },
            new() { OverallPosition = 4, Class = "GT1", Number = "4" }
        }.AsReadOnly();

        // Act
        var result = _processor.GetCarAheadOutOfClass(driver, carPositions);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("2", result.Number);
        Assert.AreEqual(2, result.OverallPosition);
        Assert.AreEqual("GT2", result.Class);
    }

    [TestMethod]
    public void GetCarAheadOutOfClass_WithFirstPosition_ReturnsNull()
    {
        // Arrange
        var driver = new CarPosition { OverallPosition = 1, Class = "GT1" };
        var carPositions = new List<CarPosition>
        {
            new() { OverallPosition = 1, Class = "GT1", Number = "1" },
            new() { OverallPosition = 2, Class = "GT2", Number = "2" }
        }.AsReadOnly();

        // Act
        var result = _processor.GetCarAheadOutOfClass(driver, carPositions);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task EnrichCarDataAsync_WithValidMetadata_EnrichesCarData()
    {
        // Arrange
        var metadata = new CompetitorMetadata 
        { 
            Make = "Honda", 
            ModelEngine = "Civic Type R" 
        };
        var carPosition = new CarPosition 
        { 
            Number = "1", 
            Class = "GT1", 
            TransponderId = 12345, 
            DriverName = "John Doe" 
        };

        _mockCompetitorMetadataProvider
            .Setup(x => x.GetCompetitorMetadataAsync(1, "1"))
            .ReturnsAsync(metadata);

        _mockCarPositionProvider
            .Setup(x => x.GetCarByNumber("1"))
            .Returns(carPosition);

        // Setup real SessionState with EventEntries
        _sessionContext.SessionState.EventEntries.Add(new EventEntry { Number = "1", Name = "Team Red" });

        var payload = new InCarPayload
        {
            Cars = [new CarStatus { Number = "1" }]
        };

        // Act
        await _processor.EnrichCarDataAsync(payload);

        // Assert
        var car = payload.Cars.First();
        Assert.IsNotNull(car);
        Assert.AreEqual("Honda Civic Type R", car.CarType);
        Assert.AreEqual("GT1", car.Class);
        Assert.AreEqual((uint)12345, car.TransponderId);
        Assert.AreEqual("John Doe", car.Driver);
        Assert.AreEqual("Team Red", car.Team);
    }

    [TestMethod]
    public async Task EnrichCarDataAsync_WithNullMetadata_HandlesGracefully()
    {
        // Arrange
        _mockCompetitorMetadataProvider
            .Setup(x => x.GetCompetitorMetadataAsync(1, "1"))
            .ReturnsAsync((CompetitorMetadata?)null);

        _mockCarPositionProvider
            .Setup(x => x.GetCarByNumber("1"))
            .Returns((CarPosition?)null);

        var payload = new InCarPayload
        {
            Cars = [new CarStatus { Number = "1" }]
        };

        // Act & Assert - Should not throw
        await _processor.EnrichCarDataAsync(payload);

        var car = payload.Cars.First();
        Assert.IsNotNull(car);
        Assert.AreEqual(string.Empty, car.Class);
        Assert.AreEqual(string.Empty, car.Driver);
    }

    [TestMethod]
    public async Task ProcessAsync_WithNoCarPositions_DoesNotSendUpdates()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;
        _mockCarPositionProvider.Setup(x => x.GetCarPositions()).Returns(new List<CarPosition>().AsReadOnly());

        // Act
        await _processor.ProcessAsync();

        // Assert
        _mockUpdateSender.Verify(x => x.SendUpdatesAsync(
            It.Is<List<InCarPayload>>(changes => changes.Count == 0), 
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ProcessAsync_WithFlagChange_SendsUpdatesForAllCars()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Red;
        
        var carPositions = new List<CarPosition>
        {
            new() { Number = "1", ClassPosition = 1, OverallPosition = 1, Class = "GT1" }
        }.AsReadOnly();
        
        _mockCarPositionProvider.Setup(x => x.GetCarPositions()).Returns(carPositions);
        _mockCarPositionProvider.Setup(x => x.GetCarByNumber("1")).Returns(carPositions[0]);

        _mockCompetitorMetadataProvider
            .Setup(x => x.GetCompetitorMetadataAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync((CompetitorMetadata?)null);

        // Simulate flag change by processing twice
        await _processor.ProcessAsync(); // First time establishes baseline
        _sessionContext.SessionState.CurrentFlag = Flags.Yellow; // Change flag
        
        // Act
        await _processor.ProcessAsync();

        // Assert
        _mockUpdateSender.Verify(x => x.SendUpdatesAsync(
            It.Is<List<InCarPayload>>(changes => changes.Count > 0), 
            It.IsAny<CancellationToken>()), Times.AtLeast(1));
    }

    [TestMethod]
    public void GetLastFlag_ReturnsCurrentFlag()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;
        _mockCarPositionProvider.Setup(x => x.GetCarPositions()).Returns(new List<CarPosition>().AsReadOnly());

        // Act
        _processor.ProcessAsync().Wait();
        var lastFlag = _processor.GetLastFlag();

        // Assert
        Assert.AreEqual(Flags.Green, lastFlag);
    }

    [TestMethod]
    public void GetCarSetsLookup_AfterProcessing_ContainsCarData()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;
        
        var carPositions = new List<CarPosition>
        {
            new() { Number = "1", ClassPosition = 1, OverallPosition = 1, Class = "GT1" }
        }.AsReadOnly();
        
        _mockCarPositionProvider.Setup(x => x.GetCarPositions()).Returns(carPositions);
        _mockCarPositionProvider.Setup(x => x.GetCarByNumber("1")).Returns(carPositions[0]);

        _mockCompetitorMetadataProvider
            .Setup(x => x.GetCompetitorMetadataAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync((CompetitorMetadata?)null);

        // Act
        _processor.ProcessAsync().Wait();
        var carSets = _processor.GetCarSetsLookup();

        // Assert
        Assert.IsTrue(carSets.ContainsKey("1"));
    }
}