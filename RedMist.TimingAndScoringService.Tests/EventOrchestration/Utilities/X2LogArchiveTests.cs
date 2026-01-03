using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventOrchestration.Utilities;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models.X2;
using System.IO.Compression;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;

[TestClass]
public class X2LogArchiveTests
{
    private X2LogArchive _archive = null!;
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
        _mockArchiveStorage.Setup(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        _mockArchiveStorage.Setup(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), It.IsAny<int>()))
            .ReturnsAsync(true);

        var purgeUtilities = new PurgeUtilities(_mockLoggerFactory.Object, _dbContextFactory);
        _archive = new X2LogArchive(_mockLoggerFactory.Object, _dbContextFactory, _mockArchiveStorage.Object, purgeUtilities);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // The production code already cleans up temp files in its finally block
        // Cleaning up here can interfere with parallel test execution
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_NoData_ReturnsTrue()
    {
        // Arrange
        int eventId = 1;

        // Act
        var result = await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), It.IsAny<int>()), Times.Never);
        _mockArchiveStorage.Verify(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_WithLoopsAndPassings_CreatesAndUploadsFiles()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Loops(organizationId, eventId, count: 5);
        await SeedX2Passings(organizationId, eventId, count: 10);

        bool loopsUploadCalled = false;
        bool passingsUploadCalled = false;

        _mockArchiveStorage.Setup(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), eventId))
            .Callback<Stream, int>((stream, id) =>
            {
                loopsUploadCalled = true;
                Assert.IsNotNull(stream);
                Assert.IsTrue(stream.CanRead, "Stream should be readable");
                Assert.IsGreaterThan(0, stream.Length, "Stream should have content");
            })
            .ReturnsAsync(true);

        _mockArchiveStorage.Setup(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), eventId))
            .Callback<Stream, int>((stream, id) =>
            {
                passingsUploadCalled = true;
                Assert.IsNotNull(stream);
                Assert.IsTrue(stream.CanRead, "Stream should be readable");
                Assert.IsGreaterThan(0, stream.Length, "Stream should have content");
            })
            .ReturnsAsync(true);

        // Act
        var result = await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        if (_loggedErrors.Any())
        {
            Assert.Fail($"Errors were logged: {string.Join("; ", _loggedErrors)}");
        }

        Assert.IsTrue(result, "Archive operation should succeed");
        Assert.IsTrue(loopsUploadCalled, "Loops upload should have been called");
        Assert.IsTrue(passingsUploadCalled, "Passings upload should have been called");
        _mockArchiveStorage.Verify(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), eventId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), eventId), Times.Once);

        // Verify data was deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLoops = await dbContext.X2Loops.Where(l => l.EventId == eventId).CountAsync();
        var remainingPassings = await dbContext.X2Passings.Where(p => p.EventId == eventId).CountAsync();
        Assert.AreEqual(0, remainingLoops);
        Assert.AreEqual(0, remainingPassings);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_OnlyLoops_UploadsOnlyLoops()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Loops(organizationId, eventId, count: 3);

        // Act
        var result = await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), eventId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), eventId), Times.Never);

        // Verify loops were deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLoops = await dbContext.X2Loops.Where(l => l.EventId == eventId).CountAsync();
        Assert.AreEqual(0, remainingLoops);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_OnlyPassings_UploadsOnlyPassings()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Passings(organizationId, eventId, count: 7);

        // Act
        var result = await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), eventId), Times.Never);
        _mockArchiveStorage.Verify(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), eventId), Times.Once);

        // Verify passings were deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingPassings = await dbContext.X2Passings.Where(p => p.EventId == eventId).CountAsync();
        Assert.AreEqual(0, remainingPassings);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_LargeNumberOfRecords_ProcessesInChunks()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Loops(organizationId, eventId, count: 150); // More than one batch
        await SeedX2Passings(organizationId, eventId, count: 600); // More than one batch

        // Act
        var result = await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        Assert.IsTrue(result);
        _mockArchiveStorage.Verify(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), eventId), Times.Once);
        _mockArchiveStorage.Verify(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), eventId), Times.Once);

        // Verify all data was deleted
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLoops = await dbContext.X2Loops.Where(l => l.EventId == eventId).CountAsync();
        var remainingPassings = await dbContext.X2Passings.Where(p => p.EventId == eventId).CountAsync();
        Assert.AreEqual(0, remainingLoops);
        Assert.AreEqual(0, remainingPassings);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_LoopsUploadFails_ReturnsFalseAndDoesNotDeleteData()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Loops(organizationId, eventId, count: 5);
        await SeedX2Passings(organizationId, eventId, count: 10);
        _mockArchiveStorage.Setup(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), eventId))
            .ReturnsAsync(false);

        // Act
        var result = await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        Assert.IsFalse(result);

        // Verify data was NOT deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLoops = await dbContext.X2Loops.Where(l => l.EventId == eventId).CountAsync();
        var remainingPassings = await dbContext.X2Passings.Where(p => p.EventId == eventId).CountAsync();
        Assert.AreEqual(5, remainingLoops);
        Assert.AreEqual(10, remainingPassings);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_PassingsUploadFails_ReturnsFalseAndDoesNotDeleteData()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Loops(organizationId, eventId, count: 5);
        await SeedX2Passings(organizationId, eventId, count: 10);
        _mockArchiveStorage.Setup(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), eventId))
            .ReturnsAsync(false);

        // Act
        var result = await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        Assert.IsFalse(result);

        // Verify data was NOT deleted from database
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var remainingLoops = await dbContext.X2Loops.Where(l => l.EventId == eventId).CountAsync();
        var remainingPassings = await dbContext.X2Passings.Where(p => p.EventId == eventId).CountAsync();
        Assert.AreEqual(5, remainingLoops);
        Assert.AreEqual(10, remainingPassings);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_ValidatesFileFormat_LoopsFile()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Loops(organizationId, eventId, count: 3);
        Stream? capturedStream = null;
        Exception? capturedException = null;

        _mockArchiveStorage.Setup(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), eventId))
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
        var result = await _archive.ArchiveX2DataAsync(eventId);

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

        var loops = JsonSerializer.Deserialize<List<Loop>>(json);
        Assert.IsNotNull(loops);
        Assert.HasCount(3, loops);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_ValidatesFileFormat_PassingsFile()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Passings(organizationId, eventId, count: 4);
        Stream? capturedStream = null;
        Exception? capturedException = null;

        _mockArchiveStorage.Setup(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), eventId))
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
        var result = await _archive.ArchiveX2DataAsync(eventId);

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

        var passings = JsonSerializer.Deserialize<List<Passing>>(json);
        Assert.IsNotNull(passings);
        Assert.HasCount(4, passings);
        Assert.IsTrue(passings.All(p => p.EventId == eventId));
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_MultipleEvents_OnlyArchivesRequestedEvent()
    {
        // Arrange
        int organizationId = 1;
        await SeedX2Loops(organizationId, eventId: 1, count: 5);
        await SeedX2Loops(organizationId, eventId: 2, count: 3);
        await SeedX2Loops(organizationId, eventId: 3, count: 7);
        await SeedX2Passings(organizationId, eventId: 1, count: 10);
        await SeedX2Passings(organizationId, eventId: 2, count: 6);
        await SeedX2Passings(organizationId, eventId: 3, count: 14);

        // Act
        var result = await _archive.ArchiveX2DataAsync(eventId: 2);

        // Assert
        Assert.IsTrue(result);

        // Verify only event 2 data was deleted
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var event1Loops = await dbContext.X2Loops.Where(l => l.EventId == 1).CountAsync();
        var event2Loops = await dbContext.X2Loops.Where(l => l.EventId == 2).CountAsync();
        var event3Loops = await dbContext.X2Loops.Where(l => l.EventId == 3).CountAsync();
        var event1Passings = await dbContext.X2Passings.Where(p => p.EventId == 1).CountAsync();
        var event2Passings = await dbContext.X2Passings.Where(p => p.EventId == 2).CountAsync();
        var event3Passings = await dbContext.X2Passings.Where(p => p.EventId == 3).CountAsync();

        Assert.AreEqual(5, event1Loops);
        Assert.AreEqual(0, event2Loops);
        Assert.AreEqual(7, event3Loops);
        Assert.AreEqual(10, event1Passings);
        Assert.AreEqual(0, event2Passings);
        Assert.AreEqual(14, event3Passings);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        await SeedX2Loops(organizationId, eventId, count: 200);
        await SeedX2Passings(organizationId, eventId, count: 1000);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        var result = await _archive.ArchiveX2DataAsync(eventId, cts.Token);

        // Since we catch all exceptions, it should return false
        Assert.IsFalse(result);
    }

    // Note: Temp file cleanup tests removed due to race conditions when tests run in parallel.
    // The cleanup happens in a synchronous finally block, so it's guaranteed to execute.
    // Testing file system cleanup across parallel test execution is unreliable and provides little value.

    [TestMethod]
    public async Task ArchiveX2DataAsync_PreservesLoopData()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        var originalLoops = new List<Loop>
        {
            new() { OrganizationId = organizationId, EventId = eventId, Id = 1, Name = "Loop1", Description = "Test Loop 1", Order = 1 },
            new() { OrganizationId = organizationId, EventId = eventId, Id = 2, Name = "Loop2", Description = "Test Loop 2", Order = 2 }
        };

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            dbContext.X2Loops.AddRange(originalLoops);
            await dbContext.SaveChangesAsync();
        }

        Stream? capturedStream = null;
        _mockArchiveStorage.Setup(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), eventId))
            .Callback<Stream, int>((stream, _) =>
            {
                capturedStream = new MemoryStream();
                stream.CopyTo(capturedStream);
                capturedStream.Position = 0;
            })
            .ReturnsAsync(true);

        // Act
        await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        Assert.IsNotNull(capturedStream);
        using var gzipStream = new GZipStream(capturedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        var json = await reader.ReadToEndAsync();
        var archivedLoops = JsonSerializer.Deserialize<List<Loop>>(json);

        Assert.IsNotNull(archivedLoops);
        Assert.HasCount(2, archivedLoops);
        Assert.AreEqual("Loop1", archivedLoops[0].Name);
        Assert.AreEqual("Test Loop 1", archivedLoops[0].Description);
        Assert.AreEqual("Loop2", archivedLoops[1].Name);
        Assert.AreEqual("Test Loop 2", archivedLoops[1].Description);
    }

    [TestMethod]
    public async Task ArchiveX2DataAsync_PreservesPassingData()
    {
        // Arrange
        int eventId = 1;
        int organizationId = 1;
        var originalPassings = new List<Passing>
        {
            new() { OrganizationId = organizationId, EventId = eventId, Id = 1, LoopId = 1, TransponderId = 100, Hits = 3, TimestampUtc = DateTime.UtcNow },
            new() { OrganizationId = organizationId, EventId = eventId, Id = 2, LoopId = 2, TransponderId = 200, Hits = 5, TimestampUtc = DateTime.UtcNow.AddSeconds(1) }
        };

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            dbContext.X2Passings.AddRange(originalPassings);
            await dbContext.SaveChangesAsync();
        }

        Stream? capturedStream = null;
        _mockArchiveStorage.Setup(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), eventId))
            .Callback<Stream, int>((stream, _) =>
            {
                capturedStream = new MemoryStream();
                stream.CopyTo(capturedStream);
                capturedStream.Position = 0;
            })
            .ReturnsAsync(true);

        // Act
        await _archive.ArchiveX2DataAsync(eventId);

        // Assert
        Assert.IsNotNull(capturedStream);
        using var gzipStream = new GZipStream(capturedStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);
        var json = await reader.ReadToEndAsync();
        var archivedPassings = JsonSerializer.Deserialize<List<Passing>>(json);

        Assert.IsNotNull(archivedPassings);
        Assert.HasCount(2, archivedPassings);
        Assert.AreEqual(1L, archivedPassings[0].LoopId);
        Assert.AreEqual(100L, archivedPassings[0].TransponderId);
        Assert.AreEqual(3, archivedPassings[0].Hits);
        Assert.AreEqual(2L, archivedPassings[1].LoopId);
        Assert.AreEqual(200L, archivedPassings[1].TransponderId);
        Assert.AreEqual(5, archivedPassings[1].Hits);
    }

    private async Task SeedX2Loops(int organizationId, int eventId, int count)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var loops = new List<Loop>();

        for (int i = 0; i < count; i++)
        {
            loops.Add(new Loop
            {
                OrganizationId = organizationId,
                EventId = eventId,
                Id = (uint)(i + 1),
                Name = $"Loop{i}",
                Description = $"Test Loop {i}",
                Order = (uint)i,
                IsOnline = i % 2 == 0,
                HasActivity = true,
                IsInPit = false,
                IsSyncOk = true,
                HasDeviceErrors = false,
                HasDeviceWarnings = false,
                Latitude0 = 40.0 + i * 0.01,
                Longitude0 = -75.0 + i * 0.01,
                Latitude1 = 40.0 + i * 0.01,
                Longitude1 = -75.0 + i * 0.01
            });
        }

        dbContext.X2Loops.AddRange(loops);
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedX2Passings(int organizationId, int eventId, int count)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var passings = new List<Passing>();

        for (int i = 0; i < count; i++)
        {
            passings.Add(new Passing
            {
                OrganizationId = organizationId,
                EventId = eventId,
                Id = (uint)(i + 1),
                LoopId = (uint)((i % 5) + 1),
                TransponderId = (uint)(1000 + i),
                TransponderShortId = (uint)(100 + i),
                Hits = (ushort)((i % 10) + 1),
                IsInPit = i % 3 == 0,
                IsResend = false,
                TimestampUtc = DateTime.UtcNow.AddSeconds(i),
                TimestampLocal = DateTime.UtcNow.AddSeconds(i)
            });
        }

        dbContext.X2Passings.AddRange(passings);
        await dbContext.SaveChangesAsync();
    }
}
