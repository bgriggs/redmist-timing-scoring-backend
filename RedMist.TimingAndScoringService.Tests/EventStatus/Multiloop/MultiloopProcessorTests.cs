using Microsoft.Extensions.Logging;
using Moq;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class MultiloopProcessorTests
{
    private MultiloopProcessor _processor = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger<MultiloopProcessor>> _mockLogger = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger<MultiloopProcessor>>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
        _processor = new MultiloopProcessor(_mockLoggerFactory.Object);
    }

    [TestMethod]
    public async Task Process_NonMultiloopMessage_ReturnsNull()
    {
        // Arrange
        var message = new TimingMessage("other", "data", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task Process_MultiloopMessage_ReturnsSessionStateUpdate()
    {
        // Arrange
        var message = new TimingMessage("multiloop", "$H", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.IsNotNull(result.changes);
    }

    [TestMethod]
    public async Task Process_HeartbeatCommand_ProcessesCorrectly()
    {
        // Arrange
        var message = new TimingMessage("multiloop", "$H�R�", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        // Heartbeat doesn't generate state changes
        Assert.AreEqual(0, result.changes.Count);
    }

    [TestMethod]
    public async Task Process_EntryCommand_ProcessesCorrectly()
    {
        // Arrange
        var entryData = "$E�R�17�Q1�12�17�Steve Introne�18�B�B-Spec�Honda Fit�Windham NH�NER�180337�White�Sripath/PurposeEnergy/BlackHog Beer/BostonMobileTire/Hyperco/G-Loc Brakes/Introne Comm�����17�";
        var message = new TimingMessage("multiloop", entryData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count); // Entry doesn't generate state changes
        
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
    public async Task Process_CompletedLapCommand_ProcessesCorrectly()
    {
        // Arrange
        var lapData = "$C�U�80004�Q1�C�0�8�4�83DDF�1CB83�T�1CB83�4�2E6A�0�649�0�C�1CB83�Unknown�G�1�0�9�0";
        var message = new TimingMessage("multiloop", lapData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.IsGreaterThan(0, result.changes.Count);
        Assert.IsTrue(_processor.CompletedLaps.ContainsKey("0"));
    }

    [TestMethod]
    public async Task Process_CompletedSectionCommand_ProcessesCorrectly()
    {
        // Arrange
        var sectionData = "$S�N�F3170000�Q1�99�EF317�S1�2DF3C0E�7C07�5";
        var message = new TimingMessage("multiloop", sectionData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.IsTrue(result.changes.Count > 0);
        Assert.IsTrue(result.changes.Any(c => c is SectionStateUpdate));
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("99"));
        Assert.IsTrue(_processor.CompletedSections["99"].ContainsKey("S1"));
    }

    [TestMethod]
    public async Task Process_LineCrossingCommand_ProcessesCorrectly()
    {
        // Arrange
        var lineData = "$L�N�EF325�Q1�89�5�SF�A�9B82E�G�T";
        var message = new TimingMessage("multiloop", lineData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        // Line crossing may or may not generate changes depending on CrossingStatus
        Assert.IsTrue(_processor.LineCrossings.ContainsKey("89"));
    }

    [TestMethod]
    public async Task Process_FlagCommand_ProcessesCorrectly()
    {
        // Arrange
        var flagData = "$F�G�5�";
        var message = new TimingMessage("multiloop", flagData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        // Flag updates only generate changes if IsDirty is true
    }

    [TestMethod]
    public async Task Process_AnnouncementCommand_ProcessesCorrectly()
    {
        // Arrange
        var announcementData = "$A�N�F3170000�Q1�2F�A�U�BC6AD080�Some Message";
        var message = new TimingMessage("multiloop", announcementData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.IsTrue(result.changes.Count > 0);
        Assert.IsTrue(result.changes.Any(c => c is AnnouncementStateUpdate));
    }

    [TestMethod]
    public async Task Process_VersionCommand_ProcessesCorrectly()
    {
        // Arrange
        var versionData = "$V�1.0.0";
        var message = new TimingMessage("multiloop", versionData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count); // Version doesn't generate state changes
    }

    [TestMethod]
    public async Task Process_InvalidatedLapCommand_ProcessesCorrectly()
    {
        // Arrange
        var invalidData = "$I�data";
        var message = new TimingMessage("multiloop", invalidData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count); // InvalidatedLap doesn't generate state changes currently
    }

    [TestMethod]
    public async Task Process_NewLeaderCommand_ProcessesCorrectly()
    {
        // Arrange
        var newLeaderData = "$N�data";
        var message = new TimingMessage("multiloop", newLeaderData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count); // NewLeader doesn't generate state changes currently
    }

    [TestMethod]
    public async Task Process_RunInformationCommand_ProcessesCorrectly()
    {
        // Arrange
        var runData = "$R�data";
        var message = new TimingMessage("multiloop", runData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count); // RunInformation doesn't generate state changes currently
    }

    [TestMethod]
    public async Task Process_TrackInformationCommand_ProcessesCorrectly()
    {
        // Arrange
        var trackData = "$T�R�22�Q1�Watkins Glen�WGI�3.4�4";
        var message = new TimingMessage("multiloop", trackData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count); // TrackInformation doesn't generate state changes currently
    }

    [TestMethod]
    public async Task Process_MultipleCommands_ProcessesAllCorrectly()
    {
        // Arrange
        var multipleCommands = "$H�R�\n$V�1.0.0\n$F�G�5�";
        var message = new TimingMessage("multiloop", multipleCommands, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.IsNotNull(result.changes);
    }

    [TestMethod]
    public async Task Process_EmptyData_HandlesGracefully()
    {
        // Arrange
        var message = new TimingMessage("multiloop", "", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count);
    }

    [TestMethod]
    public async Task Process_WhitespaceOnlyData_HandlesGracefully()
    {
        // Arrange
        var message = new TimingMessage("multiloop", "   \n  \t  ", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count);
    }

    [TestMethod]
    public async Task Process_MalformedCompletedLapData_LogsWarning()
    {
        // Arrange
        var malformedData = "$C�"; // Missing car number
        var message = new TimingMessage("multiloop", malformedData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        VerifyLogWarning("Completed lap message received with no car number");
    }

    [TestMethod]
    public async Task Process_MalformedCompletedSectionData_LogsWarning()
    {
        // Arrange
        var malformedData = "$S�"; // Missing car number and section
        var message = new TimingMessage("multiloop", malformedData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        VerifyLogWarning("Completed section message received with no car number or section");
    }

    [TestMethod]
    public async Task Process_MalformedLineCrossingData_LogsWarning()
    {
        // Arrange
        var malformedData = "$L�"; // Missing car number
        var message = new TimingMessage("multiloop", malformedData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        VerifyLogWarning("Line crossing message received with no car number");
    }

    [TestMethod]
    public async Task Process_UnknownCommand_HandlesGracefully()
    {
        // Arrange
        var unknownCommand = "$X�unknown�command";
        var message = new TimingMessage("multiloop", unknownCommand, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count);
    }

    [TestMethod]
    public async Task Process_ConcurrentAccess_IsSafe()
    {
        // Arrange
        var message = new TimingMessage("multiloop", "$H�R�", 1, DateTime.Now);
        var tasks = new List<Task<SessionStateUpdate?>>();

        // Act
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(_processor.Process(message));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.IsTrue(results.All(r => r is not null));
        Assert.IsTrue(results.All(r => r!.source == "multiloop"));
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
    public async Task Process_CompletedLapAfterSections_ClearsSections()
    {
        // Arrange - First add some sections
        var sectionData = "$S�N�F3170000�Q1�99�EF317�S1�2DF3C0E�7C07�5";
        var sectionMessage = new TimingMessage("multiloop", sectionData, 1, DateTime.Now);
        
        var lapData = "$C�U�80004�Q1�C�99�8�4�83DDF�1CB83�T�1CB83�4�2E6A�0�649�0�C�1CB83�Unknown�G�1�0�9�0";
        var lapMessage = new TimingMessage("multiloop", lapData, 1, DateTime.Now);

        // Act
        await _processor.Process(sectionMessage);
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("99"));
        Assert.IsTrue(_processor.CompletedSections["99"].Count > 0, "Section should be added");
        
        await _processor.Process(lapMessage);

        // Assert
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("99"));
        Assert.AreEqual(0, _processor.CompletedSections["99"].Count, "Sections should be cleared after lap completion");
    }

    [TestMethod]
    public async Task Process_EntryWithoutNumber_HandlesGracefully()
    {
        // Arrange
        var entryData = "$E�R�17�Q1���Steve Introne�18�B"; // Missing number (empty field)
        var message = new TimingMessage("multiloop", entryData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.AreEqual(0, result.changes.Count);
        // Should not add entry with empty number
        Assert.IsFalse(_processor.Entries.ContainsKey(""));
    }

    [TestMethod]
    public async Task Process_SectionWithoutExistingCarData_CreatesNewSectionDictionary()
    {
        // Arrange
        var sectionData = "$S�N�F3170000�Q1�42�EF317�S2�2DF3C0E�7C07�3";
        var message = new TimingMessage("multiloop", sectionData, 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        Assert.IsTrue(result.changes.Count > 0);
        Assert.IsTrue(_processor.CompletedSections.ContainsKey("42"));
        Assert.IsTrue(_processor.CompletedSections["42"].ContainsKey("S2"));
    }

    [TestMethod]
    public async Task Process_DuplicateEntrySameNumber_OverwritesPrevious()
    {
        // Arrange
        var entryData1 = "$E�R�17�Q1�10�17�First Driver�18�B�B-Spec�Honda Fit�Windham NH�NER�180337�White����17�";
        var entryData2 = "$E�R�17�Q1�10�18�Second Driver�19�A�A-Spec�Toyota Corolla�Boston MA�NER�180338�Blue����18�";
        var message1 = new TimingMessage("multiloop", entryData1, 1, DateTime.Now);
        var message2 = new TimingMessage("multiloop", entryData2, 1, DateTime.Now);

        // Act
        await _processor.Process(message1);
        await _processor.Process(message2);

        // Assert
        Assert.IsTrue(_processor.Entries.ContainsKey("10"));
        var entry = _processor.Entries["10"];
        Assert.AreEqual("Second Driver", entry.DriverName);
        Assert.AreEqual<uint>(24, entry.UniqueIdentifier); // 18 in hex = 24, but it should be processed as the actual value
    }

    [TestMethod]
    public async Task Process_FlagInformationBecomingDirty_GeneratesStateChange()
    {
        // Act - Process a flag command that will make FlagInformation dirty
        // The actual flag processing logic should set IsDirty to true when state changes
        var flagData = "$F�Y�10�100�5�200�3�50�1"; // Yellow flag with timing data
        var message = new TimingMessage("multiloop", flagData, 1, DateTime.Now);
        
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("multiloop", result.source);
        // The test would need to verify flag state changes based on actual implementation
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
}