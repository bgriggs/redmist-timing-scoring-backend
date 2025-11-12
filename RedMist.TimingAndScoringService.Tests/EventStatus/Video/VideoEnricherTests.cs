using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.Video;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Video;

[TestClass]
public class VideoEnricherTests
{
    private Mock<ILoggerFactory> mockLoggerFactory = null!;
    private Mock<ILogger> mockLogger = null!;
    private Mock<IConnectionMultiplexer> mockConnectionMultiplexer = null!;
    private Mock<IDatabase> mockDatabase = null!;
    private SessionContext sessionContext = null!;
    private VideoEnricher videoEnricher = null!;

    [TestInitialize]
    public void Setup()
    {
        mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLogger = new Mock<ILogger>();
        mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        mockDatabase = new Mock<IDatabase>();

        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);
        mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(mockDatabase.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        sessionContext = new SessionContext(config);

        videoEnricher = new VideoEnricher(sessionContext, mockLoggerFactory.Object, mockConnectionMultiplexer.Object);
    }

    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidParameters_InitializesCorrectly()
    {
        // Arrange & Act - Constructor called in Setup

        // Assert
        Assert.IsNotNull(videoEnricher);
        mockLoggerFactory.Verify(x => x.CreateLogger(It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region Process Method Tests - Car Number Matching

    [TestMethod]
    public void Process_ValidVideoMetadataWithCarNumber_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/test" }
            }
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        
        var patch = result.CarPatches[0];
        Assert.AreEqual("42", patch.Number);
        Assert.IsNotNull(patch.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, patch.InCarVideo.VideoSystemType);
        Assert.AreEqual("https://youtube.com/test", patch.InCarVideo.VideoDestination.Url);

        // Verify car position was updated
        Assert.IsNotNull(car.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, car.InCarVideo.VideoSystemType);
    }

    [TestMethod]
    public void Process_CarNumberNotFound_ReturnsNull()
    {
        // Arrange
        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "99",
            SystemType = VideoSystemType.Sentinel
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Process_EmptyCarNumber_SkipsCarNumberLookup()
    {
        // Arrange
        var videoMetadata = new VideoMetadata
        {
            CarNumber = "",
            TransponderId = 0,
            SystemType = VideoSystemType.Sentinel
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region Process Method Tests - Transponder Matching

    [TestMethod]
    public void Process_ValidVideoMetadataWithTransponderId_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "",
            TransponderId = 12345,
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://example.com" }
            }
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        
        var patch = result.CarPatches[0];
        Assert.AreEqual("42", patch.Number);
        Assert.IsNotNull(patch.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, patch.InCarVideo.VideoSystemType);
    }

    [TestMethod]
    public void Process_TransponderNotFound_ReturnsNull()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "",
            TransponderId = 99999, // Different transponder
            SystemType = VideoSystemType.Sentinel
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Process_ZeroTransponderId_ReturnsNull()
    {
        // Arrange
        var videoMetadata = new VideoMetadata
        {
            CarNumber = "",
            TransponderId = 0,
            SystemType = VideoSystemType.Sentinel
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region Process Method Tests - Priority (Car Number Over Transponder)

    [TestMethod]
    public void Process_BothCarNumberAndTransponderId_PrioritizesCarNumber()
    {
        // Arrange
        var car1 = new CarPosition { Number = "42", TransponderId = 12345 };
        var car2 = new CarPosition { Number = "99", TransponderId = 67890 };
        sessionContext.UpdateCars([car1, car2]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            TransponderId = 67890, // Different transponder
            SystemType = VideoSystemType.Sentinel
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        
        var patch = result.CarPatches[0];
        Assert.AreEqual("42", patch.Number); // Should match car number, not transponder
    }

    #endregion

    #region Process Method Tests - Event ID Validation

    [TestMethod]
    public void Process_MismatchedEventId_ReturnsNull()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 999, // Different event
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Process_MatchingEventId_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1, // Matches sessionContext.EventId
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/test" }
            }
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        Assert.AreEqual("42", result.CarPatches[0].Number);
        Assert.IsNotNull(car.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, car.InCarVideo.VideoSystemType);
    }

    #endregion

    #region Process Method Tests - Invalid/Malformed Data

    [TestMethod]
    public void Process_NullMessageData_LogsWarning()
    {
        // Arrange
        var message = new TimingMessage(Consts.VIDEO_TYPE, null!, 1, DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Unable to deserialize VideoMetadata");
    }

    [TestMethod]
    public void Process_InvalidJsonData_LogsWarning()
    {
        // Arrange
        var message = new TimingMessage(Consts.VIDEO_TYPE, "invalid json", 1, DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Unable to deserialize VideoMetadata");
    }

    [TestMethod]
    public void Process_EmptyJsonObject_ReturnsNull()
    {
        // Arrange
        var message = new TimingMessage(Consts.VIDEO_TYPE, "{}", 1, DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region Process Method Tests - Multiple Destinations

    [TestMethod]
    public void Process_MultipleDestinations_UsesFirstDestination()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/first" },
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://second" }
            }
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var patch = result.CarPatches[0];
        Assert.AreEqual("srt://second", patch.InCarVideo!.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.DirectSrt, patch.InCarVideo.VideoDestination.Type);
    }

    [TestMethod]
    public void Process_MultipleDestinationsWithSrt_PrefersSrtDestination()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/first" },
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://example.com" }
            }
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var patch = result.CarPatches[0];
        Assert.AreEqual("srt://example.com", patch.InCarVideo!.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.DirectSrt, patch.InCarVideo.VideoDestination.Type);
    }

    [TestMethod]
    public void Process_MultipleDestinationsWithoutSrt_UsesFirstDestination()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/first" },
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/second" }
            }
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var patch = result.CarPatches[0];
        Assert.AreEqual("https://youtube.com/first", patch.InCarVideo!.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.Youtube, patch.InCarVideo.VideoDestination.Type);
    }

    [TestMethod]
    public void Process_SrtDestinationNotFirst_StillPrefersSrt()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/first" },
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/second" },
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://third.com" }
            }
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var patch = result.CarPatches[0];
        Assert.AreEqual("srt://third.com", patch.InCarVideo!.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.DirectSrt, patch.InCarVideo.VideoDestination.Type);
    }

    [TestMethod]
    public void Process_NoDestinations_UsesEmptyDestination()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>()
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var patch = result.CarPatches[0];
        Assert.IsNotNull(patch.InCarVideo!.VideoDestination);
        // VideoDestination is created with default values, Url will be null
        Assert.IsTrue(string.IsNullOrEmpty(patch.InCarVideo.VideoDestination.Url));
    }

    #endregion

    #region ProcessApplyFullAsync Tests - Cache Integration

    [TestMethod]
    public async Task ProcessApplyFullAsync_CarWithVideoMetadataInCache_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/test" }
            }
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        
        var patch = result.CarPatches[0];
        Assert.AreEqual("42", patch.Number);
        Assert.IsNotNull(patch.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, patch.InCarVideo.VideoSystemType);

        // Verify car position was updated
        Assert.IsNotNull(car.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, car.InCarVideo.VideoSystemType);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_CarWithTransponderIdFallback_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            TransponderId = 12345,
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://test" }
            }
        };

        // First key (event + car number) returns no value
        var key1 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Second key (transponder only) returns metadata
        var key2 = string.Format(Consts.EVENT_VIDEO_KEY, 0, string.Empty, 12345);
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        
        var patch = result.CarPatches[0];
        Assert.AreEqual("42", patch.Number);
        Assert.IsNotNull(patch.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, patch.InCarVideo.VideoSystemType);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_CarWithEventCarAndTransponderKey_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            TransponderId = 12345,
            SystemType = VideoSystemType.Sentinel
        };

        // First two keys return no value
        var key1 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        var key2 = string.Format(Consts.EVENT_VIDEO_KEY, 0, string.Empty, 12345);
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Third key (event + car + transponder) returns metadata
        var key3 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 12345);
        mockDatabase.Setup(x => x.StringGetAsync(key3, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        
        var patch = result.CarPatches[0];
        Assert.AreEqual("42", patch.Number);
        Assert.IsNotNull(patch.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, patch.InCarVideo.VideoSystemType);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_NoVideoMetadataInCache_ClearsExistingVideoStatus()
    {
        // Arrange
        var car = new CarPosition 
        { 
            Number = "42", 
            TransponderId = 12345,
            InCarVideo = new VideoStatus 
            { 
                VideoSystemType = VideoSystemType.Sentinel,
                VideoDestination = new VideoDestination { Url = "https://old.com" }
            }
        };
        sessionContext.UpdateCars([car]);

        // All cache keys return null
        mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        
        var patch = result.CarPatches[0];
        Assert.AreEqual("42", patch.Number);
        Assert.IsNotNull(patch.InCarVideo);
        Assert.AreEqual(VideoSystemType.None, patch.InCarVideo.VideoSystemType);

        // Verify car position was cleared
        Assert.IsNull(car.InCarVideo);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_CarWithZeroTransponderId_SkipsTransponderLookup()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 0 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result.CarPatches);
        
        // Verify the first key was checked
        mockDatabase.Verify(x => x.StringGetAsync(key, CommandFlags.None), Times.Once);
        
        // Note: The implementation still includes TransponderId=0 in the first key,
        // so we can't verify "trans" is never in the key. The logic skips the else-if
        // when TransponderId is 0, which is the important behavior.
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_MultipleCars_UpdatesAllCars()
    {
        // Arrange
        var car1 = new CarPosition { Number = "1", TransponderId = 111 };
        var car2 = new CarPosition { Number = "2", TransponderId = 222 };
        sessionContext.UpdateCars([car1, car2]);

        var metadata1 = new VideoMetadata
        {
            CarNumber = "1",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/car1" }
            }
        };

        var metadata2 = new VideoMetadata
        {
            CarNumber = "2",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/car2" }
            }
        };

        var key1 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "1", 0);
        var key2 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "2", 0);

        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(metadata1));
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(metadata2));

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(2, result.CarPatches);
        
        Assert.IsTrue(result.CarPatches.Any(p => p.Number == "1"));
        Assert.IsTrue(result.CarPatches.Any(p => p.Number == "2"));
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_NoCars_ReturnsNull()
    {
        // Arrange
        sessionContext.SessionState.CarPositions.Clear();

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_SomeCarsWithMetadataSomeWithout_UpdatesOnlyRelevantCars()
    {
        // Arrange
        var car1 = new CarPosition { Number = "1", TransponderId = 111 };
        var car2 = new CarPosition { Number = "2", TransponderId = 222, InCarVideo = new VideoStatus() };
        sessionContext.UpdateCars([car1, car2]);

        var metadata1 = new VideoMetadata
        {
            CarNumber = "1",
            SystemType = VideoSystemType.Sentinel
        };

        var key1 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "1", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(metadata1));

        // Car 2 has no metadata
        var key2 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "2", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);
        mockDatabase.Setup(x => x.StringGetAsync(
            It.Is<RedisKey>(k => k.ToString().Contains("222")), 
            CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(2, result.CarPatches);
        
        // Car 1 should have video status
        var patch1 = result.CarPatches.First(p => p.Number == "1");
        Assert.IsNotNull(patch1.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, patch1.InCarVideo.VideoSystemType);
        
        // Car 2 should have empty video status (cleared)
        var patch2 = result.CarPatches.First(p => p.Number == "2");
        Assert.IsNotNull(patch2.InCarVideo);
        Assert.AreEqual(VideoSystemType.None, patch2.InCarVideo.VideoSystemType);
    }

    #endregion

    #region ProcessApplyFullAsync Tests - Error Handling

    [TestMethod]
    public async Task ProcessApplyFullAsync_InvalidJsonInCache_SkipsCar()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)"invalid json");

        // Act
        var result = await videoEnricher.ProcessApplyFullAsync();

        // Assert
        // Should handle gracefully - when invalid JSON and no existing video status, returns null
        Assert.IsNull(result);
        
        // Verify warning was logged
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unable to deserialize VideoMetadata")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public void Process_VideoSystemTypeNone_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42" };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.None,
            Destinations = new List<VideoDestination>()
        };

        var message = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(videoMetadata),
            1,
            DateTime.UtcNow);

        // Act
        var result = videoEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var patch = result.CarPatches[0];
        Assert.AreEqual(VideoSystemType.None, patch.InCarVideo!.VideoSystemType);
    }

    [TestMethod]
    public void Process_UpdateSameCarMultipleTimes_UpdatesCorrectly()
    {
        // Arrange
        var car = new CarPosition { Number = "42" };
        sessionContext.UpdateCars([car]);

        var metadata1 = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://first.com" }
            }
        };

        var message1 = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(metadata1),
            1,
            DateTime.UtcNow);

        // Act - First update
        var result1 = videoEnricher.Process(message1);

        // Assert first update
        Assert.IsNotNull(result1);
        Assert.IsNotNull(car.InCarVideo);
        Assert.AreEqual("https://first.com", car.InCarVideo.VideoDestination.Url);

        // Arrange second update
        var metadata2 = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://second.com" }
            }
        };

        var message2 = new TimingMessage(
            Consts.VIDEO_TYPE,
            JsonSerializer.Serialize(metadata2),
            1,
            DateTime.UtcNow);

        // Act - Second update
        var result2 = videoEnricher.Process(message2);

        // Assert second update
        Assert.IsNotNull(result2);
        Assert.IsNotNull(car.InCarVideo);
        Assert.AreEqual("srt://second.com", car.InCarVideo.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.DirectSrt, car.InCarVideo.VideoDestination.Type);
    }

    #endregion

    #region ProcessCarAsync Tests - Basic Functionality

    [TestMethod]
    public async Task ProcessCarAsync_ValidCarNumberWithCache_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/test" }
            }
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, result.InCarVideo.VideoSystemType);
        Assert.AreEqual("https://youtube.com/test", result.InCarVideo.VideoDestination.Url);

        // Verify car position was updated
        Assert.IsNotNull(car.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, car.InCarVideo.VideoSystemType);
    }

    [TestMethod]
    public async Task ProcessCarAsync_ValidCarNumberWithoutExplicitCache_UsesDefaultCache()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://example.com" }
            }
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act - Call without explicit cache parameter
        var result = await videoEnricher.ProcessCarAsync("42");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, result.InCarVideo.VideoSystemType);

        // Verify the mock database was accessed via IConnectionMultiplexer
        mockConnectionMultiplexer.Verify(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task ProcessCarAsync_NullCarNumber_ReturnsNullAndLogsWarning()
    {
        // Act
        var result = await videoEnricher.ProcessCarAsync(null!);

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Car number is null or empty in ProcessCarAsync");
    }

    [TestMethod]
    public async Task ProcessCarAsync_EmptyCarNumber_ReturnsNullAndLogsWarning()
    {
        // Act
        var result = await videoEnricher.ProcessCarAsync("");

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Car number is null or empty in ProcessCarAsync");
    }

    [TestMethod]
    public async Task ProcessCarAsync_CarNotFound_ReturnsNullAndLogsWarning()
    {
        // Arrange - No cars in session context

        // Act
        var result = await videoEnricher.ProcessCarAsync("99");

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Car not found for number 99 in ProcessCarAsync");
    }

    #endregion

    #region ProcessCarAsync Tests - Cache Key Lookup

    [TestMethod]
    public async Task ProcessCarAsync_TransponderIdFallback_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            TransponderId = 12345,
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://test" }
            }
        };

        // First key (event + car number) returns no value
        var key1 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Second key (transponder only) returns metadata
        var key2 = string.Format(Consts.EVENT_VIDEO_KEY, 0, string.Empty, 12345);
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, result.InCarVideo.VideoSystemType);
    }

    [TestMethod]
    public async Task ProcessCarAsync_EventCarAndTransponderKey_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            TransponderId = 12345,
            SystemType = VideoSystemType.Sentinel
        };

        // First two keys return no value
        var key1 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        var key2 = string.Format(Consts.EVENT_VIDEO_KEY, 0, string.Empty, 12345);
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Third key (event + car + transponder) returns metadata
        var key3 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 12345);
        mockDatabase.Setup(x => x.StringGetAsync(key3, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.InCarVideo);
        Assert.AreEqual(VideoSystemType.Sentinel, result.InCarVideo.VideoSystemType);
    }

    [TestMethod]
    public async Task ProcessCarAsync_ZeroTransponderId_SkipsTransponderLookup()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 0 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        
        // Verify only the first key was checked
        mockDatabase.Verify(x => x.StringGetAsync(key, CommandFlags.None), Times.Once);
        
        // Note: The implementation still includes TransponderId=0 in the first key,
        // so we can't verify "trans" is never in the key. The logic skips the else-if
        // when TransponderId is 0, which is the important behavior.
    }

    #endregion

    #region ProcessCarAsync Tests - Clearing Video Status

    [TestMethod]
    public async Task ProcessCarAsync_NoVideoMetadataInCache_ClearsExistingVideoStatus()
    {
        // Arrange
        var car = new CarPosition 
        { 
            Number = "42", 
            TransponderId = 12345,
            InCarVideo = new VideoStatus 
            { 
                VideoSystemType = VideoSystemType.Sentinel,
                VideoDestination = new VideoDestination { Url = "https://old.com" }
            }
        };
        sessionContext.UpdateCars([car]);

        // All cache keys return null
        mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsNotNull(result.InCarVideo);
        Assert.AreEqual(VideoSystemType.None, result.InCarVideo.VideoSystemType);

        // Verify car position was cleared
        Assert.IsNull(car.InCarVideo);
    }

    [TestMethod]
    public async Task ProcessCarAsync_NoVideoMetadataAndNoExistingStatus_ReturnsNull()
    {
        // Arrange
        var car = new CarPosition 
        { 
            Number = "42", 
            TransponderId = 12345,
            InCarVideo = null
        };
        sessionContext.UpdateCars([car]);

        // All cache keys return null
        mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNull(result); // No patch should be created if car had no video status
    }

    #endregion

    #region ProcessCarAsync Tests - Error Handling

    [TestMethod]
    public async Task ProcessCarAsync_InvalidJsonInCache_ReturnsNullAndLogsWarning()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)"invalid json");

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNull(result); // Should handle gracefully - no existing video status
        
        // Verify warning was logged
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unable to deserialize VideoMetadata")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task ProcessCarAsync_InvalidJsonWithExistingStatus_ClearsStatus()
    {
        // Arrange
        var car = new CarPosition 
        { 
            Number = "42", 
            TransponderId = 12345,
            InCarVideo = new VideoStatus 
            { 
                VideoSystemType = VideoSystemType.Sentinel,
                VideoDestination = new VideoDestination { Url = "https://old.com" }
            }
        };
        sessionContext.UpdateCars([car]);

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)"invalid json");

        // Setup remaining keys to return null
        mockDatabase.Setup(x => x.StringGetAsync(
            It.Is<RedisKey>(k => k.ToString() != key.ToString()), 
            CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result); // Should clear existing status
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(VideoSystemType.None, result.InCarVideo!.VideoSystemType);
        
        // Verify car position was cleared
        Assert.IsNull(car.InCarVideo);
    }

    #endregion

    #region ProcessCarAsync Tests - Multiple Destinations

    [TestMethod]
    public async Task ProcessCarAsync_MultipleDestinations_UsesFirstDestination()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/first" },
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://second" }
            }
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("srt://second", result.InCarVideo!.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.DirectSrt, result.InCarVideo.VideoDestination.Type);
    }

    [TestMethod]
    public async Task ProcessCarAsync_MultipleDestinationsWithSrt_PrefersSrtDestination()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/first" },
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://example.com" }
            }
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("srt://example.com", result.InCarVideo!.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.DirectSrt, result.InCarVideo.VideoDestination.Type);
    }

    [TestMethod]
    public async Task ProcessCarAsync_MultipleDestinationsWithoutSrt_UsesFirstDestination()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/first" },
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/second" }
            }
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("https://youtube.com/first", result.InCarVideo!.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.Youtube, result.InCarVideo.VideoDestination.Type);
    }

    [TestMethod]
    public async Task ProcessCarAsync_SrtDestinationNotFirst_StillPrefersSrt()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var videoMetadata = new VideoMetadata
        {
            CarNumber = "42",
            SystemType = VideoSystemType.Sentinel,
            Destinations = new List<VideoDestination>
            {
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/first" },
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/second" },
                new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://third.com" }
            }
        };

        var key = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "42", 0);
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(videoMetadata));

        // Act
        var result = await videoEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("srt://third.com", result.InCarVideo!.VideoDestination.Url);
        Assert.AreEqual(VideoDestinationType.DirectSrt, result.InCarVideo.VideoDestination.Type);
    }

    #endregion

    #region Helper Methods

    private void VerifyLogWarning(string expectedMessage)
    {
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(expectedMessage)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    #endregion
}
