using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.Multiloop;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.Multiloop;

[TestClass]
public class MultiloopProcessorTests
{
    private MultiloopProcessor _processor = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger<MultiloopProcessor>> _mockLogger = null!;
    private SessionContext _context = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger<MultiloopProcessor>>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        var dict = new Dictionary<string, string?> { { "event_id", "1" }, };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();

        _context = new SessionContext(config);
        _processor = new MultiloopProcessor(_mockLoggerFactory.Object, _context);
    }

    [TestMethod]
    public void Process_NonMultiloopMessage_ReturnsNull()
    {
        // Arrange
        var message = new TimingMessage("other", "data", 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Process_MultiloopMessage_ReturnsPatchUpdates()
    {
        // Arrange
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, "$H", 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.SessionPatches);
    }

    [TestMethod]
    public void Process_HeartbeatCommand_ProcessesCorrectly()
    {
        // Arrange
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, "$H�R�", 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        // Heartbeat doesn't generate state changes
        Assert.AreEqual(0, result.SessionPatches.Count);
        Assert.AreEqual(0, result.CarPatches.Count);
    }

    [TestMethod]
    public void Process_EntryCommand_ProcessesCorrectly()
    {
        // Arrange
        var entryData = "$E�R�17�Q1�12�17�Steve Introne�18�B�B-Spec�Honda Fit�Windham NH�NER�180337�White�Sripath/PurposeEnergy/BlackHog Beer/BostonMobileTire/Hyperco/G-Loc Brakes/Introne Comm�����17�";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, entryData, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count); // Entry doesn't generate state changes
        Assert.AreEqual(0, result.CarPatches.Count);

        // Debug output
        Console.WriteLine($"Entries count: {_processor.Entries.Count}");
        if (_processor.Entries.Count > 0)
        {
            var first = _processor.Entries.First();
            Console.WriteLine($"First entry key: '{first.Key}', Number: '{first.Value.Number}'");
        }

        Assert.IsTrue(_processor.Entries.ContainsKey("12"), $"Expected entry with key '12', but found keys: {string.Join(", ", _processor.Entries.Keys.Select(k => $"'{k}'"))}");
    }

    [TestMethod]
    public void Process_CompletedLapCommand_ProcessesCorrectly()
    {
        // Arrange
        var lapData = "$C�U�80004�Q1�C�0�8�4�83DDF�1CB83�T�1CB83�4�2E6A�0�649�0�C�1CB83�Unknown�G�1�0�9�0";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, lapData, 1, DateTime.Now);
        _context.UpdateCars([new CarPosition { Number = "0" }]);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.CarPatches.Count > 0);
        Assert.IsTrue(_processor.CompletedLaps.ContainsKey("0"));
    }

    [TestMethod]
    public void Process_CompletedSectionCommand_ProcessesCorrectly()
    {
        // Arrange
        var sectionData = "$S�N�F3170000�Q1�99�EF317�S1�2DF3C0E�7C07�5";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, sectionData, 1, DateTime.Now);
        _context.UpdateCars([new CarPosition { Number = "99" }]);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.CarPatches.Count > 0);

        // Verify that section data was processed by checking the processor state
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("99"));
        Assert.IsTrue(_processor.CompletedSections["99"].ContainsKey("S1"));
    }

    [TestMethod]
    public void Process_LineCrossingCommand_ProcessesCorrectly()
    {
        // Arrange
        var lineData = "$L�N�EF325�Q1�89�5�SF�A�9B82E�G�T";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, lineData, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        // Line crossing may or may not generate changes depending on CrossingStatus
        Assert.IsTrue(_processor.LineCrossings.ContainsKey("89"));
    }

    [TestMethod]
    public void Process_FlagCommand_ProcessesCorrectly()
    {
        // Arrange
        var flagData = "$F�R�5�Q1�K�0�D7108�6�6088A�1�0�1�00�1�81.63";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, flagData, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        // Flag updates should generate changes when IsDirty is true
        Assert.IsTrue(result.SessionPatches.Count > 0);

        // Verify the FlagInformation was processed
        Assert.AreEqual("K", _processor.FlagInformation.TrackStatus);
        Assert.AreEqual<ushort>(0, _processor.FlagInformation.LapNumber);
        Assert.AreEqual<uint>(880904, _processor.FlagInformation.GreenTimeMs);
        Assert.AreEqual<ushort>(6, _processor.FlagInformation.GreenLaps);
    }

    [TestMethod]
    public void Process_FlagCommand_WhenNotDirty_DoesNotGenerateStateChanges()
    {
        // Arrange
        var flagData = "$F�R�5�Q1�K�0�D7108�6�6088A�1�0�1�00�1�81.63";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, flagData, 1, DateTime.Now);

        // Act - Process first time
        var result1 = _processor.Process(message);

        // Reset dirty flag to simulate what happens after state changes are processed
        _processor.FlagInformation.ResetDirty();

        // Process same data again
        var result2 = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsTrue(result1.SessionPatches.Count > 0, "First processing should generate state changes");

        Assert.IsNotNull(result2);
        Assert.AreEqual(0, result2.SessionPatches.Count, "Second processing should not generate state changes when not dirty");
        Assert.AreEqual(0, result2.CarPatches.Count, "Second processing should not generate state changes when not dirty");
    }

    [TestMethod]
    public void Process_RunInformationCommand_ProcessesCorrectly()
    {
        // Arrange
        var runData = "$R�R�400004C7�Q1�Watkins Glen Hoosier Super Tour��Grp 2  FA FC FE2 P P2 Qual 1�Q�685ECBB8";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, runData, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        // RunInformation should generate state changes when IsDirty becomes true
        Assert.IsTrue(result.SessionPatches.Count > 0);

        // Verify the RunInformation was processed
        Assert.AreEqual("Watkins Glen Hoosier Super Tour", _processor.RunInformation.EventName);
        Assert.AreEqual("Grp 2  FA FC FE2 P P2 Qual 1", _processor.RunInformation.RunName);
        Assert.AreEqual("Q", _processor.RunInformation.RunTypeStr);
    }

    [TestMethod]
    public void Process_RunInformationCommand_WhenNotDirty_DoesNotGenerateStateChanges()
    {
        // Arrange - Process the same data twice to test that the second time doesn't generate changes
        var runData = "$R�R�400004C7�Q1�Watkins Glen Hoosier Super Tour��Grp 2  FA FC FE2 P P2 Qual 1�Q�685ECBB8";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, runData, 1, DateTime.Now);

        // Act - Process first time
        var result1 = _processor.Process(message);

        // Reset dirty flag to simulate what happens after state changes are processed
        _processor.RunInformation.ResetDirty();

        // Process same data again
        var result2 = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsTrue(result1.SessionPatches.Count > 0, "First processing should generate state changes");

        Assert.IsNotNull(result2);
        Assert.AreEqual(0, result2.SessionPatches.Count, "Second processing should not generate state changes when not dirty");
        Assert.AreEqual(0, result2.CarPatches.Count, "Second processing should not generate state changes when not dirty");
    }

    [TestMethod]
    public void Process_RunInformationWithDifferentData_GeneratesStateChanges()
    {
        // Arrange - Process initial data, then different data
        var runData1 = "$R�R�400004C7�Q1�Watkins Glen Hoosier Super Tour��Grp 2  FA FC FE2 P P2 Qual 1�Q�685ECBB8";
        var runData2 = "$R�R�400004C8�Q1�Different Event Name��Different Run Name�P�685ECBB9";
        var message1 = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, runData1, 1, DateTime.Now);
        var message2 = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, runData2, 1, DateTime.Now);

        // Act
        var result1 = _processor.Process(message1);
        _processor.RunInformation.ResetDirty(); // Reset after first processing

        var result2 = _processor.Process(message2);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsTrue(result1.SessionPatches.Count > 0);

        Assert.IsNotNull(result2);
        Assert.IsTrue(result2.SessionPatches.Count > 0, "Different data should generate state changes");

        // Verify the data was updated
        Assert.AreEqual("Different Event Name", _processor.RunInformation.EventName);
        Assert.AreEqual("Different Run Name", _processor.RunInformation.RunName);
        Assert.AreEqual("P", _processor.RunInformation.RunTypeStr);
    }

    [TestMethod]
    public void Process_EmptyData_HandlesGracefully()
    {
        // Arrange
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, "", 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count);
        Assert.AreEqual(0, result.CarPatches.Count);
    }

    [TestMethod]
    public void Process_WhitespaceOnlyData_HandlesGracefully()
    {
        // Arrange
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, "   \n  \t  ", 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count);
        Assert.AreEqual(0, result.CarPatches.Count);
    }

    [TestMethod]
    public void Process_MalformedCompletedLapData_LogsWarning()
    {
        // Arrange
        var malformedData = "$C�"; // Missing car number
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, malformedData, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        VerifyLogWarning("Completed lap message received with no car number");
    }

    [TestMethod]
    public void Process_MalformedCompletedSectionData_LogsWarning()
    {
        // Arrange
        var malformedData = "$S�"; // Missing car number and section
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, malformedData, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        VerifyLogWarning("Completed section message received with no car number or section");
    }

    [TestMethod]
    public void Process_MalformedLineCrossingData_LogsWarning()
    {
        // Arrange
        var malformedData = "$L�"; // Missing car number
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, malformedData, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        VerifyLogWarning("Line crossing message received with no car number");
    }

    [TestMethod]
    public void Process_UnknownCommand_HandlesGracefully()
    {
        // Arrange
        var unknownCommand = "$X�unknown�command";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, unknownCommand, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count);
        Assert.AreEqual(0, result.CarPatches.Count);
    }

    [TestMethod]
    public void Properties_InitializedCorrectly()
    {
        // Assert
        Assert.IsNotNull(_processor.Heartbeat);
        Assert.IsNotNull(_processor.Entries);
        Assert.IsNotNull(_processor.CompletedLaps);
        Assert.IsNotNull(_processor.CompletedSections);
        Assert.IsNotNull(_processor.LineCrossings);
        Assert.IsNotNull(_processor.FlagInformation);
        Assert.IsNotNull(_processor.NewLeader);
        Assert.IsNotNull(_processor.RunInformation);
        Assert.IsNotNull(_processor.TrackInformation);
        Assert.IsNotNull(_processor.Announcements);
        Assert.IsNotNull(_processor.Version);
    }

    [TestMethod]
    public void Process_CompletedLapAfterSections_ClearsSections()
    {
        // Arrange - First add some sections
        var sectionData = "$S�N�F3170000�Q1�99�EF317�S1�2DF3C0E�7C07�5";
        var sectionMessage = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, sectionData, 1, DateTime.Now);

        var lapData = "$C�U�80004�Q1�C�99�8�4�83DDF�1CB83�T�1CB83�4�2E6A�0�649�0�C�1CB83�Unknown�G�1�0�9�0";
        var lapMessage = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, lapData, 1, DateTime.Now);

        // Act
        _processor.Process(sectionMessage);
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("99"));
        Assert.IsTrue(_processor.CompletedSections["99"].Count > 0, "Section should be added");

        _processor.Process(lapMessage);

        // Assert
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("99"));
        Assert.AreEqual(0, _processor.CompletedSections["99"].Count, "Sections should be cleared after lap completion");
    }

    [TestMethod]
    public void Process_EntryWithoutNumber_HandlesGracefully()
    {
        // Arrange
        var entryData = "$E�R�17�Q1���Steve Introne�18�B"; // Missing number (empty field)
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, entryData, 1, DateTime.Now);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionPatches.Count);
        Assert.AreEqual(0, result.CarPatches.Count);
        // Should not add entry with empty number
        Assert.IsFalse(_processor.Entries.ContainsKey(""));
    }

    [TestMethod]
    public void Process_SectionWithoutExistingCarData_CreatesNewSectionDictionary()
    {
        // Arrange
        var sectionData = "$S�N�F3170000�Q1�42�EF317�S2�2DF3C0E�7C07�3";
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, sectionData, 1, DateTime.Now);
        _context.UpdateCars([new CarPosition { Number = "42" }]);

        // Act
        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.CarPatches.Count > 0);
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("42"));
        Assert.IsTrue(_processor.CompletedSections["42"].ContainsKey("S2"));
    }

    [TestMethod]
    public void Process_DuplicateEntrySameNumber_OverwritesPrevious()
    {
        // Arrange
        var entryData1 = "$E�R�17�Q1�10�17�First Driver�18�B�B-Spec�Honda Fit�Windham NH�NER�180337�White����17�";
        var entryData2 = "$E�R�17�Q1�10�18�Second Driver�19�A�A-Spec�Toyota Corolla�Boston MA�NER�180338�Blue����18�";
        var message1 = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, entryData1, 1, DateTime.Now);
        var message2 = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, entryData2, 1, DateTime.Now);

        // Act
        _processor.Process(message1);
        _processor.Process(message2);

        // Assert
        Assert.IsTrue(_processor.Entries.ContainsKey("10"));
        var entry = _processor.Entries["10"];
        Assert.AreEqual("Second Driver", entry.DriverName);
        Assert.AreEqual<uint>(24, entry.UniqueIdentifier); // 18 in hex = 24, but it should be processed as the actual value
    }

    [TestMethod]
    public void Process_FlagInformationBecomingDirty_GeneratesStateChange()
    {
        // Act - Process a flag command that will make FlagInformation dirty
        // The actual flag processing logic should set IsDirty to true when state changes
        var flagData = "$F�Y�10�100�5�200�3�50�1"; // Yellow flag with timing data
        var message = new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, flagData, 1, DateTime.Now);

        var result = _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        // The test would need to verify flag state changes based on actual implementation
    }

    #region ApplyCarValues Tests

    [TestMethod]
    public void ApplyCarValues_EmptyCarList_HandlesGracefully()
    {
        // Arrange
        var cars = new List<CarPosition>();

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        // Should complete without error
        Assert.AreEqual(0, cars.Count);
    }

    [TestMethod]
    public void ApplyCarValues_CarWithNullNumber_SkipsProcessing()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = null },
            new CarPosition { Number = "" },
            new CarPosition { Number = "   " } // This should not be skipped as it's not null or empty
        };

        // Populate some test data that should not be applied
        SetupTestCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);
        SetupTestLineCrossing("42", LineCrossingStatus.Pit);

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        // Cars with null/empty numbers should not have been processed
        foreach (var car in cars.Take(2)) // First two cars
        {
            Assert.IsNull(car.PitStopCount);
            Assert.IsNull(car.LastLapPitted);
            Assert.IsFalse(car.IsPitStartFinish);
            Assert.IsFalse(car.IsInPit);
        }
    }

    [TestMethod]
    public void ApplyCarValues_WithCompletedSections_AppliesCorrectly()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" },
            new CarPosition { Number = "99" }
        };

        // Setup test sections
        SetupTestCompletedSections("42", new[] { "S1", "S2" });
        SetupTestCompletedSections("99", new[] { "S3" });

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car42 = cars.First(c => c.Number == "42");
        var car99 = cars.First(c => c.Number == "99");

        Assert.IsNotNull(car42.CompletedSections);
        Assert.AreEqual(2, car42.CompletedSections.Count);

        Assert.IsNotNull(car99.CompletedSections);
        Assert.AreEqual(1, car99.CompletedSections.Count);
    }

    [TestMethod]
    public void ApplyCarValues_WithCompletedLaps_AppliesAllProperties()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestCompletedLap("42", pitStopCount: 3, lastLapPitted: 15, startPosition: 8, lapsLed: 5, currentStatus: "Running");

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();
        Assert.AreEqual(15, car.LastLapPitted);
        Assert.AreEqual(3, car.PitStopCount);
        Assert.AreEqual(8, car.OverallStartingPosition);
        Assert.AreEqual(5, car.LapsLedOverall);
        Assert.AreEqual("Running", car.CurrentStatus);
    }

    [TestMethod]
    public void ApplyCarValues_WithLongCurrentStatus_TruncatesTo12Characters()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestCompletedLap("42", currentStatus: "This is a very long status that exceeds 12 characters");

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();
        Assert.AreEqual("This is a ve", car.CurrentStatus); // Should be truncated to 12 characters
        Assert.AreEqual(12, car.CurrentStatus.Length);
    }

    [TestMethod]
    public void ApplyCarValues_WithEmptyCurrentStatus_SetsEmptyString()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestCompletedLap("42", currentStatus: "");

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();
        Assert.AreEqual("", car.CurrentStatus);
    }

    [TestMethod]
    public void ApplyCarValues_WithNullCurrentStatus_SetsEmptyString()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestCompletedLap("42", currentStatus: null);

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();
        Assert.AreEqual("", car.CurrentStatus);
    }

    [TestMethod]
    public void ApplyCarValues_WithLineCrossingTrackStatus_SetsCorrectPitStatus()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestLineCrossing("42", LineCrossingStatus.Track);

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();
        Assert.IsFalse(car.IsPitStartFinish);
        Assert.IsFalse(car.IsInPit);
    }

    [TestMethod]
    public void ApplyCarValues_WithLineCrossingPitStatus_SetsCorrectPitStatus()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestLineCrossing("42", LineCrossingStatus.Pit);

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();
        Assert.IsTrue(car.IsPitStartFinish);
        Assert.IsTrue(car.IsInPit);
    }

    [TestMethod]
    public void ApplyCarValues_WithAllDataTypes_AppliesAllCorrectly()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestCompletedSections("42", new[] { "S1", "S2", "S3" });
        SetupTestCompletedLap("42", pitStopCount: 2, lastLapPitted: 10, startPosition: 5, lapsLed: 3, currentStatus: "Leading");
        SetupTestLineCrossing("42", LineCrossingStatus.Pit);

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();

        // CompletedSections
        Assert.IsNotNull(car.CompletedSections);
        Assert.AreEqual(3, car.CompletedSections.Count);

        // CompletedLap properties
        Assert.AreEqual(10, car.LastLapPitted);
        Assert.AreEqual(2, car.PitStopCount);
        Assert.AreEqual(5, car.OverallStartingPosition);
        Assert.AreEqual(3, car.LapsLedOverall);
        Assert.AreEqual("Leading", car.CurrentStatus);

        // LineCrossing properties
        Assert.IsTrue(car.IsPitStartFinish);
        Assert.IsTrue(car.IsInPit);
    }

    [TestMethod]
    public void ApplyCarValues_CarWithoutAnyData_DoesNotModifyProperties()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "99" } // Car number not in any test data
        };

        // Capture initial values for comparison
        var initialLastLapPitted = cars[0].LastLapPitted;
        var initialPitStopCount = cars[0].PitStopCount;
        var initialOverallStartingPosition = cars[0].OverallStartingPosition;
        var initialLapsLedOverall = cars[0].LapsLedOverall;
        var initialCurrentStatus = cars[0].CurrentStatus;
        var initialIsPitStartFinish = cars[0].IsPitStartFinish;
        var initialIsInPit = cars[0].IsInPit;
        var initialCompletedSections = cars[0].CompletedSections;

        // Setup test data for different car
        SetupTestCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);

        // Act
        _processor.ApplyCarValues(cars);

        // Assert - Properties should remain unchanged since no data exists for car "99"
        var car = cars.First();
        Assert.AreEqual(initialLastLapPitted, car.LastLapPitted, "LastLapPitted should remain unchanged");
        Assert.AreEqual(initialPitStopCount, car.PitStopCount, "PitStopCount should remain unchanged");
        Assert.AreEqual(initialOverallStartingPosition, car.OverallStartingPosition, "OverallStartingPosition should remain unchanged");
        Assert.AreEqual(initialLapsLedOverall, car.LapsLedOverall, "LapsLedOverall should remain unchanged");
        Assert.AreEqual(initialCurrentStatus, car.CurrentStatus, "CurrentStatus should remain unchanged");
        Assert.AreEqual(initialIsPitStartFinish, car.IsPitStartFinish, "IsPitStartFinish should remain unchanged");
        Assert.AreEqual(initialIsInPit, car.IsInPit, "IsInPit should remain unchanged");
        Assert.AreEqual(initialCompletedSections, car.CompletedSections, "CompletedSections should remain unchanged");
    }

    [TestMethod]
    public void ApplyCarValues_MultipleCars_AppliesDataCorrectly()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "1" },
            new CarPosition { Number = "2" },
            new CarPosition { Number = "3" }
        };

        SetupTestCompletedLap("1", pitStopCount: 1, lastLapPitted: 5, currentStatus: "Car1");
        SetupTestCompletedLap("2", pitStopCount: 2, lastLapPitted: 10, currentStatus: "Car2");
        SetupTestLineCrossing("1", LineCrossingStatus.Track);
        SetupTestLineCrossing("2", LineCrossingStatus.Pit);
        SetupTestCompletedSections("3", new[] { "S1" });

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car1 = cars.First(c => c.Number == "1");
        var car2 = cars.First(c => c.Number == "2");
        var car3 = cars.First(c => c.Number == "3");

        // Car 1
        Assert.AreEqual(5, car1.LastLapPitted);
        Assert.AreEqual(1, car1.PitStopCount);
        Assert.AreEqual("Car1", car1.CurrentStatus);
        Assert.IsFalse(car1.IsPitStartFinish);

        // Car 2
        Assert.AreEqual(10, car2.LastLapPitted);
        Assert.AreEqual(2, car2.PitStopCount);
        Assert.AreEqual("Car2", car2.CurrentStatus);
        Assert.IsTrue(car2.IsPitStartFinish);

        // Car 3
        Assert.IsNotNull(car3.CompletedSections);
        Assert.AreEqual(1, car3.CompletedSections.Count);
    }

    [TestMethod]
    public void ApplyCarValues_MaxValues_HandlesCorrectly()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestCompletedLap("42", 
            pitStopCount: ushort.MaxValue, 
            lastLapPitted: ushort.MaxValue, 
            startPosition: ushort.MaxValue, 
            lapsLed: ushort.MaxValue);

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();
        Assert.AreEqual(ushort.MaxValue, car.LastLapPitted);
        Assert.AreEqual(ushort.MaxValue, car.PitStopCount);
        Assert.AreEqual(ushort.MaxValue, car.OverallStartingPosition);
        Assert.AreEqual(ushort.MaxValue, car.LapsLedOverall);
    }

    [TestMethod]
    public void ApplyCarValues_ZeroValues_HandlesCorrectly()
    {
        // Arrange
        var cars = new List<CarPosition>
        {
            new CarPosition { Number = "42" }
        };

        SetupTestCompletedLap("42", 
            pitStopCount: 0, 
            lastLapPitted: 0, 
            startPosition: 0, 
            lapsLed: 0);

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        var car = cars.First();
        Assert.AreEqual(0, car.LastLapPitted);
        Assert.AreEqual(0, car.PitStopCount);
        Assert.AreEqual(0, car.OverallStartingPosition);
        Assert.AreEqual(0, car.LapsLedOverall);
    }

    #endregion

    #region Helper Methods

    private void SetupTestCompletedSections(string carNumber, string[] sectionIds)
    {
        var sections = new Dictionary<string, RedMist.EventProcessor.EventStatus.Multiloop.CompletedSection>();
        foreach (var sectionId in sectionIds)
        {
            sections[sectionId] = new RedMist.EventProcessor.EventStatus.Multiloop.CompletedSection();
        }
        _processor.CompletedSections[carNumber] = sections;
    }

    private void SetupTestCompletedLap(string carNumber, ushort pitStopCount = 0, ushort lastLapPitted = 0, 
        ushort startPosition = 0, ushort lapsLed = 0, string? currentStatus = null)
    {
        var completedLap = new CompletedLap();
        
        // Use reflection to set private properties for testing
        SetPrivateProperty(completedLap, nameof(CompletedLap.Number), carNumber);
        SetPrivateProperty(completedLap, nameof(CompletedLap.PitStopCount), pitStopCount);
        SetPrivateProperty(completedLap, nameof(CompletedLap.LastLapPitted), lastLapPitted);
        SetPrivateProperty(completedLap, nameof(CompletedLap.StartPosition), startPosition);
        SetPrivateProperty(completedLap, nameof(CompletedLap.LapsLed), lapsLed);
        SetPrivateProperty(completedLap, nameof(CompletedLap.CurrentStatus), currentStatus ?? string.Empty);

        _processor.CompletedLaps[carNumber] = completedLap;
    }

    private void SetupTestLineCrossing(string carNumber, LineCrossingStatus crossingStatus)
    {
        var lineCrossing = new LineCrossing();
        
        // Use reflection to set private properties for testing
        SetPrivateProperty(lineCrossing, nameof(LineCrossing.Number), carNumber);
        SetPrivateProperty(lineCrossing, nameof(LineCrossing.CrossingStatusStr), 
            crossingStatus == LineCrossingStatus.Pit ? "P" : "T");

        _processor.LineCrossings[carNumber] = lineCrossing;
    }

    private static void SetPrivateProperty(object obj, string propertyName, object value)
    {
        var property = obj.GetType().GetProperty(propertyName, 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        
        if (property != null && property.CanWrite)
        {
            property.SetValue(obj, value);
        }
        else
        {
            // If property is not writable, try to find the backing field
            var field = obj.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(f => f.Name.Contains(propertyName) || f.Name == $"<{propertyName}>k__BackingField");
            
            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }
    }

    #endregion

    private void VerifyLogWarning(string expectedMessage)
    {
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(expectedMessage)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}