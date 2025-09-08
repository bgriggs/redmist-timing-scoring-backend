using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.PositionEnricher;
using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using RedMist.TimingCommon.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.PositionEnricher;

[TestClass]
public class PositionDataEnricherTests
{
    private Mock<IDbContextFactory<TsContext>> _mockDbContextFactory = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private SessionContext _sessionContext = null!;
    private PositionDataEnricher _enricher = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockDbContextFactory = new Mock<IDbContextFactory<TsContext>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();

        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        var dict = new Dictionary<string, string?> { { "event_id", "1" }, };

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();

        _sessionContext = new SessionContext(config);
        _enricher = new PositionDataEnricher(_mockDbContextFactory.Object, _mockLoggerFactory.Object, _sessionContext);
    }

    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidParameters_InitializesCorrectly()
    {
        // Act & Assert - Constructor called in Setup, no exception should be thrown
        Assert.IsNotNull(_enricher);
        _mockLoggerFactory.Verify(x => x.CreateLogger(It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region Process Tests

    [TestMethod]
    public void Process_EmptyCarPositions_ReturnsNull()
    {
        // Arrange
        _sessionContext.SessionState.CarPositions.Clear();
        var sessionStateUpdate = new SessionStateUpdate([], []);

        // Act
        var result = _enricher.Process(sessionStateUpdate);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Process_SingleCarWithPositionMetadataChanges_ReturnsSessionStateUpdate()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "A", 1);
        car.TotalTime = "00:10:00.000";
        car.LastLapCompleted = 10;
        car.OverallPosition = 1;
        car.ClassPosition = 0; // This will be updated by the processor

        _sessionContext.SessionState.CarPositions.Add(car);
        var sessionStateUpdate = new SessionStateUpdate([], [new CarLapStateUpdate(new TimingAndScoringService.EventStatus.RMonitor.RaceInformation())]);

        // Act
        var result = _enricher.Process(sessionStateUpdate);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionChanges.Count);
        Assert.IsTrue(result.CarChanges.Count > 0);

        var carChange = result.CarChanges.First();
        var patch = carChange.GetChanges(car);
        Assert.IsNotNull(patch);
        Assert.AreEqual("1", patch.Number);
    }

    [TestMethod]
    public void Process_MultipleCarsSingleClass_UpdatesClassPositions()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "A", 1);
        car1.TotalTime = "00:10:00.000";
        car1.OverallPosition = 1;
        car1.ClassPosition = 0; // Will be updated to 1

        var car2 = CreateTestCarPosition("2", "A", 2);
        car2.TotalTime = "00:10:01.000";
        car2.OverallPosition = 2;
        car2.ClassPosition = 0; // Will be updated to 2

        _sessionContext.SessionState.CarPositions.AddRange([car1, car2]);
        var sessionStateUpdate = new SessionStateUpdate([], [new CarLapStateUpdate(new TimingAndScoringService.EventStatus.RMonitor.RaceInformation())]);

        // Act
        var result = _enricher.Process(sessionStateUpdate);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.CarChanges.Count);

        // Verify class positions were set
        var car1Patch = result.CarChanges.First().GetChanges(car1);
        var car2Patch = result.CarChanges.Last().GetChanges(car2);

        Assert.AreEqual(1, car1Patch?.ClassPosition);
        Assert.AreEqual(2, car2Patch?.ClassPosition);
    }

    [TestMethod]
    public void Process_MultipleClassesWithGapCalculation_UpdatesGapsAndDifferences()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "A", 1);
        car1.TotalTime = "00:10:00.000";
        car1.OverallPosition = 1;
        car1.LastLapCompleted = 10;

        var car2 = CreateTestCarPosition("2", "A", 2);
        car2.TotalTime = "00:10:01.000";
        car2.OverallPosition = 2;
        car2.LastLapCompleted = 10;

        var car3 = CreateTestCarPosition("3", "B", 3);
        car3.TotalTime = "00:10:02.000";
        car3.OverallPosition = 3;
        car3.LastLapCompleted = 10;

        _sessionContext.SessionState.CarPositions.AddRange([car1, car2, car3]);
        var sessionStateUpdate = new SessionStateUpdate([], [new CarLapStateUpdate(new TimingAndScoringService.EventStatus.RMonitor.RaceInformation())]);

        // Act
        var result = _enricher.Process(sessionStateUpdate);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.CarChanges.Count);

        // Verify gaps and differences were calculated
        var car2Patch = result.CarChanges.Skip(1).First().GetChanges(car2);
        var car3Patch = result.CarChanges.Skip(2).First().GetChanges(car3);

        Assert.AreEqual("1.000", car2Patch?.OverallGap);
        Assert.AreEqual("1.000", car2Patch?.OverallDifference);
        Assert.AreEqual("1.000", car3Patch?.OverallGap);
        Assert.AreEqual("2.000", car3Patch?.OverallDifference);
    }

    [TestMethod]
    public void Process_BestTimeCalculation_UpdatesBestTimeFlags()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "A", 1);
        car1.BestTime = "00:01:00.000";
        car1.IsBestTime = false; // Will be updated to true
        car1.IsBestTimeClass = false; // Will be updated to true

        var car2 = CreateTestCarPosition("2", "A", 2);
        car2.BestTime = "00:01:01.000";
        car2.IsBestTime = false; // Will remain false
        car2.IsBestTimeClass = false; // Will remain false

        _sessionContext.SessionState.CarPositions.AddRange([car1, car2]);
        var sessionStateUpdate = new SessionStateUpdate([], [new CarLapStateUpdate(new TimingAndScoringService.EventStatus.RMonitor.RaceInformation())]);

        // Act
        var result = _enricher.Process(sessionStateUpdate);

        // Assert
        Assert.IsNotNull(result);

        var car1Patch = result.CarChanges.First().GetChanges(car1);
        var car2Patch = result.CarChanges.Last().GetChanges(car2);

        Assert.IsTrue(car1Patch?.IsBestTime);
        Assert.IsTrue(car1Patch?.IsBestTimeClass);
        // No change so no patch should be created
        Assert.IsNull(car2Patch?.IsBestTime);
        Assert.IsNull(car2Patch?.IsBestTimeClass);
    }

    [TestMethod]
    public void Process_PositionGainsCalculation_UpdatesPositionGains()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "A", 1);
        car1.OverallPosition = 1;
        car1.OverallStartingPosition = 3; // Gained 2 positions
        car1.ClassPosition = 1;
        car1.InClassStartingPosition = 2; // Gained 1 position in class
        car1.OverallPositionsGained = CarPosition.InvalidPosition; // Will be updated
        car1.InClassPositionsGained = CarPosition.InvalidPosition; // Will be updated

        _sessionContext.SessionState.CarPositions.Add(car1);
        var sessionStateUpdate = new SessionStateUpdate([], [new CarLapStateUpdate(new TimingAndScoringService.EventStatus.RMonitor.RaceInformation())]);

        // Act
        var result = _enricher.Process(sessionStateUpdate);

        // Assert
        Assert.IsNotNull(result);

        var car1Patch = result.CarChanges.First().GetChanges(car1);
        Assert.AreEqual(2, car1Patch?.OverallPositionsGained);
        Assert.AreEqual(1, car1Patch?.InClassPositionsGained);
    }

    [TestMethod]
    public void Process_DoesNotModifyOriginalSessionState()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "A", 1);
        car.TotalTime = "00:10:00.000";
        car.OverallPosition = 1;
        car.ClassPosition = 0; // Original value

        _sessionContext.SessionState.CarPositions.Add(car);
        var sessionStateUpdate = new SessionStateUpdate([], [new CarLapStateUpdate(new TimingAndScoringService.EventStatus.RMonitor.RaceInformation())]);

        // Act
        _enricher.Process(sessionStateUpdate);

        // Assert - Original car should not be modified
        Assert.AreEqual(0, car.ClassPosition);
        Assert.IsNull(car.OverallGap);
        Assert.IsNull(car.OverallDifference);
    }

    #endregion

    #region Clear Tests

    [TestMethod]
    public void Clear_CallsClearOnProcessor()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "A", 1);
        car.OverallPosition = 1;
        car.OverallStartingPosition = 3;
        _sessionContext.SessionState.CarPositions.Add(car);
        var sessionStateUpdate = new SessionStateUpdate([], [new CarLapStateUpdate(new TimingAndScoringService.EventStatus.RMonitor.RaceInformation())]);

        // Process once to populate internal state
        _enricher.Process(sessionStateUpdate);

        // Act
        _enricher.Clear();

        // Assert
        // After clear, processing the same car again should treat it as new
        var result = _enricher.Process(sessionStateUpdate);
        Assert.IsNotNull(result);
    }

    #endregion

    #region CarPositionMapper Tests

    [TestMethod]
    public void CarPositionMapper_CloneCarPositions_CreatesDeepCopy()
    {
        // Arrange
        var mapper = new CarPositionMapper();
        var original = CreateTestCarPosition("1", "A", 1);
        original.TotalTime = "00:10:00.000";

        var completedSection = new CompletedSection
        {
            Number = "1",
            SectionId = "Sector1",
            ElapsedTimeMs = 30000
        };
        original.CompletedSections.Add(completedSection);

        var originalList = new List<CarPosition> { original };

        // Act
        var cloned = mapper.CloneCarPositions(originalList);

        // Assert
        Assert.AreEqual(1, cloned.Count);

        // Verify the CarPosition objects are different instances using ReferenceEquals
        Assert.IsFalse(ReferenceEquals(original, cloned[0]), "Original and cloned CarPosition should be different instances");

        // Verify the properties are correctly copied
        Assert.AreEqual(original.Number, cloned[0].Number);
        Assert.AreEqual(original.TotalTime, cloned[0].TotalTime);
        Assert.AreEqual(original.Class, cloned[0].Class);
        Assert.AreEqual(original.OverallPosition, cloned[0].OverallPosition);

        // Verify CompletedSections collection is a different instance using ReferenceEquals
        Assert.IsFalse(ReferenceEquals(original.CompletedSections, cloned[0].CompletedSections),
            "Original and cloned CompletedSections collections should be different instances");

        // Verify CompletedSections collection has the same count
        Assert.AreEqual(original.CompletedSections.Count, cloned[0].CompletedSections.Count);

        // Verify the CompletedSection objects are different instances but have same values
        if (original.CompletedSections.Count > 0 && cloned[0].CompletedSections.Count > 0)
        {
            var originalSection = original.CompletedSections[0];
            var clonedSection = cloned[0].CompletedSections[0];

            // Use ReferenceEquals to verify different instances
            Assert.IsFalse(ReferenceEquals(originalSection, clonedSection),
                "Original and cloned CompletedSection should be different instances");

            // Verify the values are correctly copied
            Assert.AreEqual(originalSection.Number, clonedSection.Number);
            Assert.AreEqual(originalSection.SectionId, clonedSection.SectionId);
            Assert.AreEqual(originalSection.ElapsedTimeMs, clonedSection.ElapsedTimeMs);
        }
    }

    #endregion

    #region PositionMetadataStateUpdate Tests

    [TestMethod]
    public void PositionMetadataStateUpdate_GetChanges_ReturnsProvidedPatch()
    {
        // Arrange
        var patch = new CarPositionPatch
        {
            Number = "1",
            OverallGap = "1.000"
        };
        var stateUpdate = new PositionMetadataStateUpdate(patch);
        var dummyCarPosition = CreateTestCarPosition("1", "A", 1);

        // Act
        var result = stateUpdate.GetChanges(dummyCarPosition);

        // Assert
        Assert.AreSame(patch, result);
        Assert.AreEqual("1", result!.Number);
        Assert.AreEqual("1.000", result.OverallGap);
    }

    #endregion

    #region Helper Methods

    private static CarPosition CreateTestCarPosition(string number, string carClass, int overallPosition)
    {
        return new CarPosition
        {
            Number = number,
            Class = carClass,
            OverallPosition = overallPosition,
            TransponderId = 12345,
            EventId = "1",
            SessionId = "1",
            BestLap = 0,
            LastLapCompleted = 0,
            OverallStartingPosition = overallPosition,
            InClassStartingPosition = 1,
            OverallPositionsGained = CarPosition.InvalidPosition,
            InClassPositionsGained = CarPosition.InvalidPosition,
            ClassPosition = 0,
            PenalityLaps = 0,
            PenalityWarnings = 0,
            BlackFlags = 0,
            IsEnteredPit = false,
            IsPitStartFinish = false,
            IsExitedPit = false,
            IsInPit = false,
            LapIncludedPit = false,
            LastLoopName = string.Empty,
            IsStale = false,
            TrackFlag = Flags.Green,
            LocalFlag = Flags.Green,
            CompletedSections = [],
            ProjectedLapTimeMs = 0,
            LapStartTime = TimeOnly.MinValue,
            DriverName = string.Empty,
            DriverId = string.Empty,
            CurrentStatus = "Active",
            ImpactWarning = false,
            IsBestTime = false,
            IsBestTimeClass = false,
            IsOverallMostPositionsGained = false,
            IsClassMostPositionsGained = false
        };
    }

    #endregion
}
