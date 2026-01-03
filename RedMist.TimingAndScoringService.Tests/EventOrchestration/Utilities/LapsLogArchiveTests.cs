using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventOrchestration.Utilities;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.IO.Compression;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;

[TestClass]
public class LapsLogArchiveTests
{
    private LapsLogArchive _archive = null!;
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
            _mockArchiveStorage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(true);
            _mockArchiveStorage.Setup(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync(true);

            var purgeUtilities = new PurgeUtilities(_mockLoggerFactory.Object, _dbContextFactory);
            _archive = new LapsLogArchive(_mockLoggerFactory.Object, _dbContextFactory, _mockArchiveStorage.Object, purgeUtilities);
        }

    [TestCleanup]
    public void Cleanup()
    {
        // The production code already cleans up temp files in its finally block
        // Cleaning up here can interfere with parallel test execution
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_NoLaps_ReturnsTrue()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _mockArchiveStorage.Verify(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_WithLapsSingleCar_CreatesAndUploadsBothFiles()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        string carNumber = "42";
        await SeedCarLapLogs(eventId, sessionId, carNumber, count: 10);

        bool sessionUploadCalled = false;
        bool carUploadCalled = false;
        Exception? capturedException = null;

        _mockArchiveStorage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId))
            .Callback<Stream, int, int>((stream, _, _) =>
            {
                sessionUploadCalled = true;
                Assert.IsNotNull(stream);
                Assert.IsTrue(stream.CanRead, "Stream should be readable");
                Assert.IsGreaterThan(0, stream.Length, "Stream should have content");
            })
            .ReturnsAsync(true);

        _mockArchiveStorage.Setup(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), eventId, sessionId, carNumber))
            .Callback<Stream, int, int, string>((stream, _, _, _) =>
            {
                carUploadCalled = true;
                Assert.IsNotNull(stream);
                Assert.IsTrue(stream.CanRead, "Stream should be readable");
                Assert.IsGreaterThan(0, stream.Length, "Stream should have content");
            })
            .ReturnsAsync(true);

        // Act
        var result = false;
        try
        {
            result = await _archive.ArchiveLapsAsync(eventId, sessionId);
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
        Assert.IsTrue(sessionUploadCalled, "Session upload should have been called");
        Assert.IsTrue(carUploadCalled, "Car upload should have been called");
        _mockArchiveStorage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), eventId, sessionId, carNumber), Times.Once);

        // Verify laps were deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLaps = await dbContext.CarLapLogs.Where(l => l.EventId == eventId && l.SessionId == sessionId).CountAsync();
        Assert.AreEqual(0, remainingLaps);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_WithMultipleCars_CreatesMultipleCarFiles()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        await SeedCarLapLogs(eventId, sessionId, "42", count: 5);
        await SeedCarLapLogs(eventId, sessionId, "99", count: 7);
        await SeedCarLapLogs(eventId, sessionId, "12", count: 3);

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

        // Assert
        if (_loggedErrors.Any())
        {
            Assert.Fail($"Errors were logged: {string.Join("; ", _loggedErrors)}");
        }

        Assert.IsTrue(result, "Archive operation should succeed");
        _mockArchiveStorage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), eventId, sessionId, "42"), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), eventId, sessionId, "99"), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), eventId, sessionId, "12"), Times.Once);

        // Verify laps were deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLaps = await dbContext.CarLapLogs.Where(l => l.EventId == eventId && l.SessionId == sessionId).CountAsync();
        Assert.AreEqual(0, remainingLaps);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_LargeNumberOfLaps_ProcessesInChunks()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        await SeedCarLapLogs(eventId, sessionId, "42", count: 250); // More than 2 batches (batch size is 100)

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), eventId, sessionId, "42"), Times.Once);

        // Verify all laps were deleted
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLaps = await dbContext.CarLapLogs.Where(l => l.EventId == eventId && l.SessionId == sessionId).CountAsync();
        Assert.AreEqual(0, remainingLaps);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_SessionUploadFails_ReturnsFalseAndDoesNotDeleteLaps()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        await SeedCarLapLogs(eventId, sessionId, "42", count: 5);
        _mockArchiveStorage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId))
            .ReturnsAsync(false);

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

        // Assert
        Assert.IsFalse(result);

        // Verify laps were NOT deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLaps = await dbContext.CarLapLogs.Where(l => l.EventId == eventId && l.SessionId == sessionId).CountAsync();
        Assert.AreEqual(5, remainingLaps);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_CarUploadFails_ReturnsFalseAndDoesNotDeleteLaps()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        string carNumber = "42";
        await SeedCarLapLogs(eventId, sessionId, carNumber, count: 5);
        _mockArchiveStorage.Setup(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), eventId, sessionId, carNumber))
            .ReturnsAsync(false);

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

        // Assert
        Assert.IsFalse(result);

        // Verify laps were NOT deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLaps = await dbContext.CarLapLogs.Where(l => l.EventId == eventId && l.SessionId == sessionId).CountAsync();
        Assert.AreEqual(5, remainingLaps);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_ValidatesFileFormat_SessionFile()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        await SeedCarLapLogs(eventId, sessionId, "42", count: 3);
        Stream? capturedStream = null;
        Exception? capturedException = null;

        _mockArchiveStorage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId))
            .Callback<Stream, int, int>((stream, _, _) =>
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
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

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

            var carPositions = JsonSerializer.Deserialize<List<CarPosition>>(json);
            Assert.IsNotNull(carPositions);
            Assert.HasCount(3, carPositions);
        }

    [TestMethod]
    public async Task ArchiveLapsAsync_ValidatesFileFormat_CarFile()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        string carNumber = "42";
        await SeedCarLapLogs(eventId, sessionId, carNumber, count: 4);
        Stream? capturedStream = null;
        Exception? capturedException = null;

        _mockArchiveStorage.Setup(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), eventId, sessionId, carNumber))
            .Callback<Stream, int, int, string>((stream, _, _, _) =>
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
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

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

            var carPositions = JsonSerializer.Deserialize<List<CarPosition>>(json);
            Assert.IsNotNull(carPositions);
            Assert.HasCount(4, carPositions);
            Assert.IsTrue(carPositions.All(cp => cp.Number == carNumber));
        }

    [TestMethod]
    public async Task ArchiveLapsAsync_MultipleSessions_OnlyArchivesRequestedSession()
    {
        // Arrange
        await SeedCarLapLogs(eventId: 1, sessionId: 1, carNumber: "42", count: 5);
        await SeedCarLapLogs(eventId: 1, sessionId: 2, carNumber: "42", count: 3);
        await SeedCarLapLogs(eventId: 1, sessionId: 3, carNumber: "42", count: 7);

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId: 1, sessionId: 2);

        // Assert
        Assert.IsTrue(result);

        // Verify only session 2 laps were deleted
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var session1Laps = await dbContext.CarLapLogs.Where(l => l.EventId == 1 && l.SessionId == 1).CountAsync();
        var session2Laps = await dbContext.CarLapLogs.Where(l => l.EventId == 1 && l.SessionId == 2).CountAsync();
        var session3Laps = await dbContext.CarLapLogs.Where(l => l.EventId == 1 && l.SessionId == 3).CountAsync();

        Assert.AreEqual(5, session1Laps);
        Assert.AreEqual(0, session2Laps);
        Assert.AreEqual(7, session3Laps);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_MultipleEvents_OnlyArchivesRequestedEvent()
    {
        // Arrange
        await SeedCarLapLogs(eventId: 1, sessionId: 1, carNumber: "42", count: 5);
        await SeedCarLapLogs(eventId: 2, sessionId: 1, carNumber: "42", count: 3);
        await SeedCarLapLogs(eventId: 3, sessionId: 1, carNumber: "42", count: 7);

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId: 2, sessionId: 1);

        // Assert
        Assert.IsTrue(result);

        // Verify only event 2 laps were deleted
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var event1Laps = await dbContext.CarLapLogs.Where(l => l.EventId == 1 && l.SessionId == 1).CountAsync();
        var event2Laps = await dbContext.CarLapLogs.Where(l => l.EventId == 2 && l.SessionId == 1).CountAsync();
        var event3Laps = await dbContext.CarLapLogs.Where(l => l.EventId == 3 && l.SessionId == 1).CountAsync();

        Assert.AreEqual(5, event1Laps);
        Assert.AreEqual(0, event2Laps);
        Assert.AreEqual(7, event3Laps);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_CancellationRequested_ReturnsFalse()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        await SeedCarLapLogs(eventId, sessionId, "42", count: 1000);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId, cts.Token);

        // Since we catch all exceptions, it should return false
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_TempFilesCleanedUp_OnSuccess()
    {
        // Arrange
        int eventId = Random.Shared.Next(100000, 999999);
        int sessionId = Random.Shared.Next(100000, 999999);
        await SeedCarLapLogs(eventId, sessionId, "42", count: 5);

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

        // Assert
        Assert.IsTrue(result);

        // Give a moment for cleanup
        await Task.Delay(100);

        // Verify no temp files remain for this event/session
        var tempPath = Path.GetTempPath();
        var remainingFiles = Directory.GetFiles(tempPath, $"event-{eventId}-session-{sessionId}-*-laps-*.json*");
        Assert.IsEmpty(remainingFiles, "Temp files should be cleaned up");
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_TempFilesCleanedUp_OnFailure()
    {
        // Arrange
        int eventId = Random.Shared.Next(100000, 999999);
        int sessionId = Random.Shared.Next(100000, 999999);
        await SeedCarLapLogs(eventId, sessionId, "42", count: 5);
        _mockArchiveStorage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId))
            .ReturnsAsync(false);

        // Act
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

        // Assert
        Assert.IsFalse(result);

        // Give a moment for cleanup
        await Task.Delay(100);

        // Verify no temp files remain for this event/session
        var tempPath = Path.GetTempPath();
        var remainingFiles = Directory.GetFiles(tempPath, $"event-{eventId}-session-{sessionId}-*-laps-*.json*");
        Assert.IsEmpty(remainingFiles, "Temp files should be cleaned up even on failure");
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_PreservesLapData_InSessionFile()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;

        var carPosition1 = CreateTestCarPosition("42", "A", 1, 1);
        var carPosition2 = CreateTestCarPosition("42", "A", 1, 2);

        var originalLaps = new List<CarLapLog>
        {
            new() { EventId = eventId, SessionId = sessionId, CarNumber = "42", LapNumber = 1, Flag = 1, LapData = JsonSerializer.Serialize(carPosition1), Timestamp = DateTime.UtcNow },
            new() { EventId = eventId, SessionId = sessionId, CarNumber = "42", LapNumber = 2, Flag = 2, LapData = JsonSerializer.Serialize(carPosition2), Timestamp = DateTime.UtcNow.AddSeconds(1) }
        };

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            dbContext.CarLapLogs.AddRange(originalLaps);
            await dbContext.SaveChangesAsync();
        }

        Stream? capturedStream = null;
        _mockArchiveStorage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId))
            .Callback<Stream, int, int>((stream, _, _) =>
            {
                capturedStream = new MemoryStream();
                stream.CopyTo(capturedStream);
                capturedStream.Position = 0;
            })
            .ReturnsAsync(true);

        // Act
        await _archive.ArchiveLapsAsync(eventId, sessionId);

        // Assert
        Assert.IsNotNull(capturedStream);
        using var gzipStream = new GZipStream(capturedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        var json = await reader.ReadToEndAsync();
        var archivedCarPositions = JsonSerializer.Deserialize<List<CarPosition>>(json);

        Assert.IsNotNull(archivedCarPositions);
        Assert.HasCount(2, archivedCarPositions);
        Assert.AreEqual("42", archivedCarPositions[0].Number);
        Assert.AreEqual(1, archivedCarPositions[0].LastLapCompleted);
        Assert.AreEqual("42", archivedCarPositions[1].Number);
        Assert.AreEqual(2, archivedCarPositions[1].LastLapCompleted);
    }

    [TestMethod]
    public async Task ArchiveLapsAsync_SessionFileContainsAllCars()
    {
        // Arrange
        int eventId = 1;
        int sessionId = 1;
        await SeedCarLapLogs(eventId, sessionId, "42", count: 3);
        await SeedCarLapLogs(eventId, sessionId, "99", count: 2);
        Stream? capturedStream = null;
        Exception? capturedException = null;

        _mockArchiveStorage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), eventId, sessionId))
            .Callback<Stream, int, int>((stream, _, _) =>
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
        var result = await _archive.ArchiveLapsAsync(eventId, sessionId);

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

            using var gzipStream = new GZipStream(capturedStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream);
            var json = await reader.ReadToEndAsync();
            var carPositions = JsonSerializer.Deserialize<List<CarPosition>>(json);

            Assert.IsNotNull(carPositions);
            Assert.HasCount(5, carPositions);
            Assert.AreEqual(3, carPositions.Count(cp => cp.Number == "42"));
            Assert.AreEqual(2, carPositions.Count(cp => cp.Number == "99"));
        }

            private async Task SeedCarLapLogs(int eventId, int sessionId, string carNumber, int count)
            {
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
                var laps = new List<CarLapLog>();

                for (int i = 0; i < count; i++)
                {
                    var carPosition = CreateTestCarPosition(carNumber, "A", i + 1, i + 1);
                    var lapData = JsonSerializer.Serialize(carPosition);

                    laps.Add(new CarLapLog
                    {
                        EventId = eventId,
                        SessionId = sessionId,
                        CarNumber = carNumber,
                        LapNumber = i + 1,
                        Flag = i % 3,
                        LapData = lapData,
                        Timestamp = DateTime.UtcNow.AddSeconds(i)
                    });
                }

                dbContext.CarLapLogs.AddRange(laps);
                await dbContext.SaveChangesAsync();
            }

            private static CarPosition CreateTestCarPosition(string number, string carClass, int overallPosition, int lapNumber)
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
                    LastLapCompleted = lapNumber,
                    OverallStartingPosition = overallPosition,
                    InClassStartingPosition = 1,
                    OverallPositionsGained = CarPosition.InvalidPosition,
                    InClassPositionsGained = CarPosition.InvalidPosition,
                    ClassPosition = 1,
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
        }
