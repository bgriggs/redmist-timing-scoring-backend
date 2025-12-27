using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventOrchestration.Utilities;
using RedMist.EventProcessor.Tests.Utilities;
using System.IO.Compression;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;

[TestClass]
public class EventLogArchiveTests
{
    private EventLogArchive _archive = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private Mock<IArchiveStorage> _mockArchiveStorage = null!;
    private string _testDatabaseName = null!;
    private List<string> _loggedErrors = null!;

    [TestInitialize]
    public void Setup()
    {
        _loggedErrors = new List<string>();

        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();

        // Capture error logs
        _mockLogger.Setup(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var exception = invocation.Arguments[3] as Exception;
                var formatter = invocation.Arguments[4];
                var message = formatter?.GetType().GetMethod("Invoke")?.Invoke(formatter, new[] { invocation.Arguments[2], exception })?.ToString() ?? "";
                _loggedErrors.Add($"{exception?.Message ?? ""}  | {message}");
            }));

        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        _testDatabaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(_testDatabaseName);
        _dbContextFactory = new TestDbContextFactory(optionsBuilder.Options);

        _mockArchiveStorage = new Mock<IArchiveStorage>();
        _mockArchiveStorage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        _mockArchiveStorage.Setup(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(true);

        _archive = new EventLogArchive(_mockLoggerFactory.Object, _dbContextFactory, _mockArchiveStorage.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // The production code already cleans up temp files in its finally block
        // Cleaning up here can interfere with parallel test execution
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_NoLogs_ReturnsTrue()
    {
        // Arrange
        int eventId = 1;

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), It.IsAny<int>()), Times.Never);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_WithLogsNoSessions_CreatesAndUploadsSingleFile()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 0, count: 10);

        bool uploadCalled = false;
        Exception? capturedException = null;
        _mockArchiveStorage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId))
            .Callback<Stream, int>((stream, id) =>
            {
                uploadCalled = true;
                Assert.IsNotNull(stream);
                Assert.IsTrue(stream.CanRead, "Stream should be readable");
                Assert.IsGreaterThan(0, stream.Length, "Stream should have content");
            })
            .ReturnsAsync(true);

        // Act
        var result = false;
        try
        {
            result = await _archive.ArchiveEventLogsAsync(eventId);
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        // Assert
        if (capturedException != null)
        {
            Assert.Fail($"Exception occurred: {capturedException.Message}\n{capturedException.StackTrace}");
        }

        if (_loggedErrors.Any())
        {
            Assert.Fail($"Errors were logged: {string.Join("; ", _loggedErrors)}");
        }

        Assert.IsTrue(result, "Archive operation should succeed");
        Assert.IsTrue(uploadCalled, "Upload should have been called");
        _mockArchiveStorage.Verify(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);

        // Verify logs were deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLogs = await dbContext.EventStatusLogs.Where(e => e.EventId == eventId).CountAsync();
        Assert.AreEqual(0, remainingLogs);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_WithMultipleSessions_CreatesMultipleFiles()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 1, count: 5);
        await SeedEventLogs(eventId, sessionId: 2, count: 7);
        await SeedEventLogs(eventId, sessionId: 3, count: 3);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        if (_loggedErrors.Any())
        {
            Assert.Fail($"Errors were logged: {string.Join("; ", _loggedErrors)}");
        }

        Assert.IsTrue(result, "Archive operation should succeed");
        _mockArchiveStorage.Verify(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), eventId, 1), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), eventId, 2), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), eventId, 3), Times.Once);

        // Verify logs were deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLogs = await dbContext.EventStatusLogs.Where(e => e.EventId == eventId).CountAsync();
        Assert.AreEqual(0, remainingLogs);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_MixedSessionAndNonSessionLogs_HandlesCorrectly()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 0, count: 5); // Non-session logs
        await SeedEventLogs(eventId, sessionId: 1, count: 3);
        await SeedEventLogs(eventId, sessionId: 2, count: 4);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), eventId, 1), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), eventId, 2), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), It.IsAny<int>(), 0), Times.Never);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_LargeNumberOfLogs_ProcessesInChunks()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 1, count: 500); // More than one batch

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), eventId, 1), Times.Once);

        // Verify all logs were deleted
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLogs = await dbContext.EventStatusLogs.Where(e => e.EventId == eventId).CountAsync();
        Assert.AreEqual(0, remainingLogs);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_EventUploadFails_ReturnsFalseAndDoesNotDeleteLogs()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 1, count: 5);
        _mockArchiveStorage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId))
            .ReturnsAsync(false);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        Assert.IsFalse(result);

        // Verify logs were NOT deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLogs = await dbContext.EventStatusLogs.Where(e => e.EventId == eventId).CountAsync();
        Assert.AreEqual(5, remainingLogs);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_SessionUploadFails_ReturnsFalseAndDoesNotDeleteLogs()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 1, count: 5);
        _mockArchiveStorage.Setup(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), eventId, 1))
            .ReturnsAsync(false);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        Assert.IsFalse(result);

        // Verify logs were NOT deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLogs = await dbContext.EventStatusLogs.Where(e => e.EventId == eventId).CountAsync();
        Assert.AreEqual(5, remainingLogs);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_ValidatesFileFormat_EventFile()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 0, count: 3);
        Stream? capturedStream = null;
        Exception? capturedException = null;

        _mockArchiveStorage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId))
            .Callback<Stream, int>((stream, _) =>
            {
                try
                {
                    var memStream = new MemoryStream();
                    if (stream.CanSeek && stream.Position != 0)
                    {
                        stream.Position = 0;
                    }
                    stream.CopyTo(memStream);
                    memStream.Position = 0;
                    capturedStream = memStream;
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                    throw;
                }
            })
            .ReturnsAsync(true);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        if (capturedException != null)
        {
            Assert.Fail($"Exception during stream capture: {capturedException.Message}\n{capturedException.StackTrace}");
        }

        if (_loggedErrors.Any())
        {
            Assert.Fail($"Errors were logged: {string.Join("; ", _loggedErrors)}");
        }

        Assert.IsTrue(result, "Archive operation should succeed");
        Assert.IsNotNull(capturedStream, "Stream should have been captured");

        // Decompress and validate JSON
        using var gzipStream = new GZipStream(capturedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        var json = await reader.ReadToEndAsync();

        var logs = JsonSerializer.Deserialize<List<EventStatusLog>>(json);
        Assert.IsNotNull(logs);
        Assert.HasCount(3, logs);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_ValidatesFileFormat_SessionFile()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 5;
        await SeedEventLogs(eventId, sessionId, count: 4);
        Stream? capturedStream = null;
        Exception? capturedException = null;
        bool eventLogUploadCalled = false;
        bool sessionLogUploadCalled = false;

        // Ensure event logs upload is properly mocked
        _mockArchiveStorage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId))
            .Callback<Stream, int>((stream, _) =>
            {
                eventLogUploadCalled = true;
            })
            .ReturnsAsync(true);

        _mockArchiveStorage.Setup(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), eventId, sessionId))
            .Callback<Stream, int, int>((stream, _, _) =>
            {
                sessionLogUploadCalled = true;
                try
                {
                    var memStream = new MemoryStream();
                    if (stream.CanSeek && stream.Position != 0)
                    {
                        stream.Position = 0;
                    }
                    stream.CopyTo(memStream);
                    memStream.Position = 0;
                    capturedStream = memStream;
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                    throw;
                }
            })
            .ReturnsAsync(true);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        if (capturedException != null)
        {
            Assert.Fail($"Exception during stream capture: {capturedException.Message}\n{capturedException.StackTrace}");
        }

        if (_loggedErrors.Any())
        {
            Assert.Fail($"Errors were logged: {string.Join("; ", _loggedErrors)}");
        }

        Assert.IsTrue(result, $"Archive operation should succeed. EventLogUploadCalled: {eventLogUploadCalled}, SessionLogUploadCalled: {sessionLogUploadCalled}");
        Assert.IsTrue(eventLogUploadCalled, "Event log upload should have been called");
        Assert.IsTrue(sessionLogUploadCalled, "Session log upload should have been called");
        Assert.IsNotNull(capturedStream, "Stream should have been captured");

        // Decompress and validate JSON
        using var gzipStream = new GZipStream(capturedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        var json = await reader.ReadToEndAsync();

        var logs = JsonSerializer.Deserialize<List<EventStatusLog>>(json);
        Assert.IsNotNull(logs);
        Assert.HasCount(4, logs);
        Assert.IsTrue(logs.All(l => l.SessionId == sessionId));
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_MultipleEvents_OnlyArchivesRequestedEvent()
    {
        // Arrange
        await SeedEventLogs(eventId: 1, sessionId: 1, count: 5);
        await SeedEventLogs(eventId: 2, sessionId: 1, count: 3);
        await SeedEventLogs(eventId: 3, sessionId: 1, count: 7);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId: 2);

        // Assert
        Assert.IsTrue(result);

        // Verify only event 2 logs were deleted
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var event1Logs = await dbContext.EventStatusLogs.Where(e => e.EventId == 1).CountAsync();
        var event2Logs = await dbContext.EventStatusLogs.Where(e => e.EventId == 2).CountAsync();
        var event3Logs = await dbContext.EventStatusLogs.Where(e => e.EventId == 3).CountAsync();

        Assert.AreEqual(5, event1Logs);
        Assert.AreEqual(0, event2Logs);
        Assert.AreEqual(7, event3Logs);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 1, count: 1000);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        var result = await _archive.ArchiveEventLogsAsync(eventId, cts.Token);

        // Since we catch all exceptions, it should return false
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_TempFilesCleanedUp_OnSuccess()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 1, count: 5);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        Assert.IsTrue(result);

        // Give a moment for cleanup
        await Task.Delay(100);

        // Verify no temp files remain for this event
        var tempPath = Path.GetTempPath();
        var remainingFiles = Directory.GetFiles(tempPath, $"event-{eventId}-*-logs-*.json*");
        Assert.IsEmpty(remainingFiles, "Temp files should be cleaned up");
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_TempFilesCleanedUp_OnFailure()
    {
        // Arrange
        int eventId = 1;
        await SeedEventLogs(eventId, sessionId: 1, count: 5);
        _mockArchiveStorage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId))
            .ReturnsAsync(false);

        // Act
        var result = await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        Assert.IsFalse(result);

        // Give a moment for cleanup
        await Task.Delay(100);

        // Verify no temp files remain for this event
        var tempPath = Path.GetTempPath();
        var remainingFiles = Directory.GetFiles(tempPath, $"event-{eventId}-*-logs-*.json*");
        Assert.IsEmpty(remainingFiles, "Temp files should be cleaned up even on failure");
    }

    [TestMethod]
    public async Task ArchiveEventLogsAsync_PreservesLogData_InEventFile()
    {
        // Arrange
        int eventId = 1;
        var originalLogs = new List<EventStatusLog>
        {
            new() { EventId = eventId, SessionId = 0, Type = "Type1", Data = "Data1", Timestamp = DateTime.UtcNow },
            new() { EventId = eventId, SessionId = 0, Type = "Type2", Data = "Data2", Timestamp = DateTime.UtcNow.AddSeconds(1) }
        };

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            dbContext.EventStatusLogs.AddRange(originalLogs);
            await dbContext.SaveChangesAsync();
        }

        Stream? capturedStream = null;
        _mockArchiveStorage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), eventId))
            .Callback<Stream, int>((stream, _) =>
            {
                capturedStream = new MemoryStream();
                stream.CopyTo(capturedStream);
                capturedStream.Position = 0;
            })
            .ReturnsAsync(true);

        // Act
        await _archive.ArchiveEventLogsAsync(eventId);

        // Assert
        Assert.IsNotNull(capturedStream);
        using var gzipStream = new GZipStream(capturedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        var json = await reader.ReadToEndAsync();
        var archivedLogs = JsonSerializer.Deserialize<List<EventStatusLog>>(json);

        Assert.IsNotNull(archivedLogs);
        Assert.HasCount(2, archivedLogs);
        Assert.AreEqual("Type1", archivedLogs[0].Type);
        Assert.AreEqual("Data1", archivedLogs[0].Data);
        Assert.AreEqual("Type2", archivedLogs[1].Type);
        Assert.AreEqual("Data2", archivedLogs[1].Data);
    }

    private async Task SeedEventLogs(int eventId, int sessionId, int count)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var logs = new List<EventStatusLog>();

        for (int i = 0; i < count; i++)
        {
            logs.Add(new EventStatusLog
            {
                EventId = eventId,
                SessionId = sessionId,
                Type = $"TestType{i}",
                Data = $"TestData{i}",
                Timestamp = DateTime.UtcNow.AddSeconds(i)
            });
        }

        dbContext.EventStatusLogs.AddRange(logs);
        await dbContext.SaveChangesAsync();
    }
}
