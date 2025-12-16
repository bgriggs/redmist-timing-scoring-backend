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
public class DriverModeProcessorIntegrationTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private SessionContext _sessionContext = null!;
    private TestCarPositionProvider _carPositionProvider = null!;
    private TestCompetitorMetadataProvider _metadataProvider = null!;
    private TestInCarUpdateSender _updateSender = null!;
    private DriverModeProcessor _processor = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockLogger = new Mock<ILogger>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var dbContextFactory = CreateDbContextFactory();
        _sessionContext = new SessionContext(configuration, dbContextFactory, new FakeTimeProvider());

        var carPositions = new List<CarPosition>
        {
            new() { Number = "1", ClassPosition = 1, OverallPosition = 1, Class = "GT1", DriverName = "Driver 1" },
            new() { Number = "2", ClassPosition = 2, OverallPosition = 2, Class = "GT1", DriverName = "Driver 2" },
            new() { Number = "3", ClassPosition = 1, OverallPosition = 3, Class = "GT2", DriverName = "Driver 3" },
            new() { Number = "4", ClassPosition = 3, OverallPosition = 4, Class = "GT1", DriverName = "Driver 4" }
        };

        _carPositionProvider = new TestCarPositionProvider(carPositions);
        _metadataProvider = new TestCompetitorMetadataProvider();
        _updateSender = new TestInCarUpdateSender();

        // Add some test metadata
        _metadataProvider.AddMetadata("1", new CompetitorMetadata { Make = "Honda", ModelEngine = "Civic Type R" });
        _metadataProvider.AddMetadata("2", new CompetitorMetadata { Make = "BMW", ModelEngine = "M3 GT4" });

        // Add event entries to session context
        _sessionContext.SessionState.EventEntries.Add(new EventEntry { Number = "1", Name = "Team Alpha" });
        _sessionContext.SessionState.EventEntries.Add(new EventEntry { Number = "2", Name = "Team Beta" });
        _sessionContext.SessionState.EventEntries.Add(new EventEntry { Number = "3", Name = "Team Gamma" });
        _sessionContext.SessionState.EventEntries.Add(new EventEntry { Number = "4", Name = "Team Delta" });

        _processor = new DriverModeProcessor(
            loggerFactory: _mockLoggerFactory.Object,
            sessionContext: _sessionContext,
            carPositionProvider: _carPositionProvider,
            competitorMetadataProvider: _metadataProvider,
            updateSender: _updateSender
        );
    }

    [TestMethod]
    public async Task ProcessAsync_FirstRun_SendsUpdatesForAllCars()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Act
        await _processor.ProcessAsync();

        // Assert
        Assert.AreEqual(1, _updateSender.UpdateCallCount);
        var sentUpdates = _updateSender.GetLastSentUpdate();
        Assert.HasCount(4, sentUpdates);
        
        // Verify flag is set correctly
        foreach (var update in sentUpdates)
        {
            Assert.AreEqual(Flags.Green, update.Flag);
        }
    }

    [TestMethod]
    public async Task ProcessAsync_FlagChange_SendsUpdatesAgain()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;
        await _processor.ProcessAsync(); // Initial run
        _updateSender.Clear();

        // Act
        _sessionContext.SessionState.CurrentFlag = Flags.Yellow;
        await _processor.ProcessAsync();

        // Assert
        Assert.AreEqual(1, _updateSender.UpdateCallCount);
        var sentUpdates = _updateSender.GetLastSentUpdate();
        Assert.HasCount(4, sentUpdates);
        
        foreach (var update in sentUpdates)
        {
            Assert.AreEqual(Flags.Yellow, update.Flag);
        }
    }

    [TestMethod]
    public async Task ProcessAsync_NoChanges_DoesNotSendUpdates()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;
        await _processor.ProcessAsync(); // Initial run
        _updateSender.Clear();

        // Act
        await _processor.ProcessAsync(); // Run again with no changes

        // Assert
        Assert.AreEqual(1, _updateSender.UpdateCallCount);
        var sentUpdates = _updateSender.GetLastSentUpdate();
        Assert.IsEmpty(sentUpdates);
    }

    [TestMethod]
    public async Task ProcessAsync_VerifyCarRelationships()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Act
        await _processor.ProcessAsync();

        // Assert
        var carPositions = _carPositionProvider.GetCarPositions();
        
        // Test car ahead/behind relationships for GT1 class
        var car1 = carPositions.First(c => c.Number == "1"); // 1st in GT1
        var car2 = carPositions.First(c => c.Number == "2"); // 2nd in GT1
        var car4 = carPositions.First(c => c.Number == "4"); // 3rd in GT1

        Assert.IsNull(_processor.GetCarAhead(car1, carPositions)); // 1st place has no car ahead
        Assert.AreEqual("2", _processor.GetCarBehind(car1, carPositions)?.Number);

        Assert.AreEqual("1", _processor.GetCarAhead(car2, carPositions)?.Number);
        Assert.AreEqual("4", _processor.GetCarBehind(car2, carPositions)?.Number);

        Assert.AreEqual("2", _processor.GetCarAhead(car4, carPositions)?.Number);
        Assert.IsNull(_processor.GetCarBehind(car4, carPositions)); // Last in class
    }

    [TestMethod]
    public async Task ProcessAsync_VerifyOutOfClassRelationships()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Act
        await _processor.ProcessAsync();

        // Assert
        var carPositions = _carPositionProvider.GetCarPositions();
        var car3 = carPositions.First(c => c.Number == "3"); // 3rd overall, 1st in GT2

        // Car 3 is 3rd overall, so car ahead should be car 2 (2nd overall, different class GT1)
        var carAheadOutOfClass = _processor.GetCarAheadOutOfClass(car3, carPositions);
        Assert.IsNotNull(carAheadOutOfClass);
        Assert.AreEqual("2", carAheadOutOfClass.Number);
        Assert.AreEqual("GT1", carAheadOutOfClass.Class);
    }

    [TestMethod]
    public async Task EnrichCarDataAsync_PopulatesAllFields()
    {
        // Arrange
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
        Assert.AreEqual("Driver 1", car.Driver);
        Assert.AreEqual("Team Alpha", car.Team);
    }

    [TestMethod]
    public void GetCarSetsLookup_AfterProcessing_MaintainsState()
    {
        // Arrange
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Act
        _processor.ProcessAsync().Wait();
        var carSets = _processor.GetCarSetsLookup();

        // Assert
        Assert.HasCount(4, carSets);
        Assert.IsTrue(carSets.ContainsKey("1"));
        Assert.IsTrue(carSets.ContainsKey("2"));
        Assert.IsTrue(carSets.ContainsKey("3"));
        Assert.IsTrue(carSets.ContainsKey("4"));
    }

    [TestMethod]
    public void GetLastFlag_TracksCorrectly()
    {
        // Arrange & Act
        Assert.AreEqual(Flags.Unknown, _processor.GetLastFlag()); // Initial state

        _sessionContext.SessionState.CurrentFlag = Flags.Green;
        _processor.ProcessAsync().Wait();
        Assert.AreEqual(Flags.Green, _processor.GetLastFlag());

                _sessionContext.SessionState.CurrentFlag = Flags.Red;
                _processor.ProcessAsync().Wait();
                Assert.AreEqual(Flags.Red, _processor.GetLastFlag());
            }

            private static IDbContextFactory<TsContext> CreateDbContextFactory()
            {
                var databaseName = $"TestDatabase_{Guid.NewGuid()}";
                var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
                optionsBuilder.UseInMemoryDatabase(databaseName);
                var options = optionsBuilder.Options;
                return new TestDbContextFactory(options);
            }
        }