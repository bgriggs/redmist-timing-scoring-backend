using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.Multiloop;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
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

        var dbContextFactory = CreateDbContextFactory();
        var mockLapHistoryService = new Mock<ICarLapHistoryService>();
        _context = new SessionContext(config, dbContextFactory, _mockLoggerFactory.Object, mockLapHistoryService.Object);
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
        Assert.IsEmpty(result.SessionPatches);
        Assert.IsEmpty(result.CarPatches);
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
        Assert.IsEmpty(result.SessionPatches);
        Assert.IsEmpty(result.CarPatches);

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
        Assert.IsNotEmpty(result.CarPatches);
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
        Assert.IsNotEmpty(result.CarPatches);

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
        Assert.IsNotEmpty(result.SessionPatches);

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
        Assert.IsNotEmpty(result1.SessionPatches, "First processing should generate state changes");

        Assert.IsNotNull(result2);
        Assert.IsEmpty(result2.SessionPatches);
        Assert.IsEmpty(result2.CarPatches);
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
        Assert.IsNotEmpty(result.SessionPatches);

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
        Assert.IsNotEmpty(result1.SessionPatches, "First processing should generate state changes");

        Assert.IsNotNull(result2);
        Assert.IsEmpty(result2.SessionPatches, "Second processing should not generate state changes when not dirty");
        Assert.IsEmpty(result2.CarPatches, "Second processing should not generate state changes when not dirty");
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
        Assert.IsNotEmpty(result1.SessionPatches);

        Assert.IsNotNull(result2);
        Assert.IsNotEmpty(result2.SessionPatches);

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
        Assert.IsEmpty(result.SessionPatches);
        Assert.IsEmpty(result.CarPatches);
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
        Assert.IsEmpty(result.SessionPatches);
        Assert.IsEmpty(result.CarPatches);
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
        Assert.IsEmpty(result.SessionPatches);
        Assert.IsEmpty(result.CarPatches);
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
        Assert.IsNotEmpty(_processor.CompletedSections["99"]);

        _processor.Process(lapMessage);

        // Assert
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("99"));
        Assert.IsEmpty(_processor.CompletedSections["99"]);
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
        Assert.IsEmpty(result.SessionPatches);
        Assert.IsEmpty(result.CarPatches);
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
        Assert.IsNotEmpty(result.CarPatches);
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("42"));
        Assert.IsTrue(_processor.CompletedSections["42"].ContainsKey("S2"));
    }

    [TestMethod]
    public void ApplyCarValues_EmptyCarList_HandlesGracefully()
    {
        // Arrange
        var cars = new List<CarPosition>();

        // Act
        _processor.ApplyCarValues(cars);

        // Assert
        // Should complete without error
        Assert.IsEmpty(cars);
    }

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

        private static IDbContextFactory<TsContext> CreateDbContextFactory()
        {
            var databaseName = $"TestDatabase_{Guid.NewGuid()}";
            var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
            optionsBuilder.UseInMemoryDatabase(databaseName);
            var options = optionsBuilder.Options;
            return new TestDbContextFactory(options);
        }
    }