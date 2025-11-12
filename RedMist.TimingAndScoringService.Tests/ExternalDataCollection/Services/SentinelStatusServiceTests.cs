using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Utilities;
using RedMist.ExternalDataCollection.Clients;
using RedMist.ExternalDataCollection.Services;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Reflection;

namespace RedMist.TimingAndScoringService.Tests.ExternalDataCollection.Services;

[TestClass]
public class SentinelStatusServiceTests
{
    private Mock<ILoggerFactory> mockLoggerFactory = null!;
    private Mock<ILogger> mockLogger = null!;
    private Mock<IConnectionMultiplexer> mockConnectionMultiplexer = null!;
    private Mock<IHubContext<StatusHub>> mockHubContext = null!;
    private Mock<SentinelClient> mockSentinelClient = null!;
    private Mock<EventsChecker> mockEventsChecker = null!;
    private Mock<ExternalTelemetryClient> mockExternalTelemetryClient = null!;
    private Mock<IHttpClientFactory> mockHttpClientFactory = null!;
    private SentinelStatusService sentinelStatusService = null!;
    private IConfiguration configuration = null!;

    [TestInitialize]
    public void Setup()
    {
        mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLogger = new Mock<ILogger>();
        mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        mockHubContext = new Mock<IHubContext<StatusHub>>();
        mockHttpClientFactory = new Mock<IHttpClientFactory>();
        
        // Create a mock configuration for the clients
        configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "SentinelApiUrl", "https://test.sentinel.com" },
                { "Server:ExtTelemUrl", "https://test.exttelem.com" },
                { "Keycloak:AuthServerUrl", "https://test.keycloak.com" },
                { "Keycloak:Realm", "test-realm" },
                { "Keycloak:ClientId", "test-client" },
                { "Keycloak:ClientSecret", "test-secret" }
            })
            .Build();
        
        mockEventsChecker = new Mock<EventsChecker>(mockConnectionMultiplexer.Object);
        mockSentinelClient = new Mock<SentinelClient>(configuration);
        mockExternalTelemetryClient = new Mock<ExternalTelemetryClient>(configuration);

        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        sentinelStatusService = new SentinelStatusService(
            mockLoggerFactory.Object,
            mockConnectionMultiplexer.Object,
            mockHubContext.Object,
            mockSentinelClient.Object,
            mockEventsChecker.Object,
            mockExternalTelemetryClient.Object,
            mockHttpClientFactory.Object);
    }

    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidParameters_InitializesCorrectly()
    {
        // Arrange & Act - Constructor called in Setup

        // Assert
        Assert.IsNotNull(sentinelStatusService);
        mockLoggerFactory.Verify(x => x.CreateLogger(It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region SendDriverInfoAsync Tests - Basic Functionality

    [TestMethod]
    public async Task SendDriverInfoAsync_NewDrivers_SendsAllDrivers()
    {
        // Arrange
        var driverInfo = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 12345, DriverName = "Driver 1", DriverId = "driver-1" },
            new DriverInfo { TransponderId = 67890, DriverName = "Driver 2", DriverId = "driver-2" }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await InvokeSendDriverInfoAsync(driverInfo);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(
                It.Is<List<DriverInfo>>(list => list.Count == 2 && 
                    list.Any(d => d.TransponderId == 12345) && 
                    list.Any(d => d.TransponderId == 67890)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendDriverInfoAsync_EmptyDriverList_SendsEmptyList()
    {
        // Arrange
        var driverInfo = new List<DriverInfo>();

        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await InvokeSendDriverInfoAsync(driverInfo);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(
                It.Is<List<DriverInfo>>(list => list.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendDriverInfoAsync_NullLastDriverInfo_SendsOnlyNewDrivers()
    {
        // Arrange - lastDriverInfo is null by default
        var driverInfo = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 12345, DriverName = "Driver 1" }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await InvokeSendDriverInfoAsync(driverInfo);

        // Assert - Should only send new drivers, no removed drivers
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(
                It.Is<List<DriverInfo>>(list => list.Count == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region SendDriverInfoAsync Tests - Removed Drivers

    [TestMethod]
    public async Task SendDriverInfoAsync_DriversRemoved_SendsEmptyDriverInfo()
    {
        // Arrange - Set up previous drivers
        var previousDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 12345, DriverName = "Driver 1" },
            new DriverInfo { TransponderId = 67890, DriverName = "Driver 2" }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastDriverInfo
        await InvokeSendDriverInfoAsync(previousDrivers);

        // Act - Send with one driver removed
        var currentDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 12345, DriverName = "Driver 1" }
        };

        await InvokeSendDriverInfoAsync(currentDrivers);

        // Assert - Should send current driver plus empty entry for removed driver
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(
                It.Is<List<DriverInfo>>(list => 
                    list.Count == 2 &&
                    list.Any(d => d.TransponderId == 12345 && d.DriverName == "Driver 1") &&
                    list.Any(d => d.TransponderId == 67890 && string.IsNullOrEmpty(d.DriverName))),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendDriverInfoAsync_AllDriversRemoved_SendsOnlyEmptyEntries()
    {
        // Arrange - Set up previous drivers
        var previousDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 12345, DriverName = "Driver 1" },
            new DriverInfo { TransponderId = 67890, DriverName = "Driver 2" }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastDriverInfo
        await InvokeSendDriverInfoAsync(previousDrivers);

        // Act - Send with all drivers removed
        var currentDrivers = new List<DriverInfo>();

        await InvokeSendDriverInfoAsync(currentDrivers);

        // Assert - Should send only empty entries for removed drivers
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(
                It.Is<List<DriverInfo>>(list => 
                    list.Count == 2 &&
                    list.All(d => string.IsNullOrEmpty(d.DriverName))),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendDriverInfoAsync_MultipleRemovedDrivers_SendsAllEmptyEntries()
    {
        // Arrange - Set up previous drivers
        var previousDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 111, DriverName = "Driver 1" },
            new DriverInfo { TransponderId = 222, DriverName = "Driver 2" },
            new DriverInfo { TransponderId = 333, DriverName = "Driver 3" }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastDriverInfo
        await InvokeSendDriverInfoAsync(previousDrivers);

        // Act - Send with two drivers removed, one remaining
        var currentDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 222, DriverName = "Driver 2" }
        };

        await InvokeSendDriverInfoAsync(currentDrivers);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(
                It.Is<List<DriverInfo>>(list => 
                    list.Count == 3 &&
                    list.Any(d => d.TransponderId == 222 && d.DriverName == "Driver 2") &&
                    list.Any(d => d.TransponderId == 111 && string.IsNullOrEmpty(d.DriverName)) &&
                    list.Any(d => d.TransponderId == 333 && string.IsNullOrEmpty(d.DriverName))),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region SendDriverInfoAsync Tests - Updates

    [TestMethod]
    public async Task SendDriverInfoAsync_SameDriversUpdated_SendsUpdatedInfo()
    {
        // Arrange - Set up previous drivers
        var previousDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 12345, DriverName = "Old Name", DriverId = "old-id" }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastDriverInfo
        await InvokeSendDriverInfoAsync(previousDrivers);

        // Act - Send with same transponder but updated name
        var currentDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 12345, DriverName = "New Name", DriverId = "new-id" }
        };

        await InvokeSendDriverInfoAsync(currentDrivers);

        // Assert - Should send only the updated driver
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(
                It.Is<List<DriverInfo>>(list => 
                    list.Count == 1 &&
                    list.Any(d => d.TransponderId == 12345 && d.DriverName == "New Name" && d.DriverId == "new-id")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendDriverInfoAsync_MixedAdditionsAndRemovals_SendsCorrectList()
    {
        // Arrange - Set up previous drivers
        var previousDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 111, DriverName = "Driver 1" },
            new DriverInfo { TransponderId = 222, DriverName = "Driver 2" }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastDriverInfo
        await InvokeSendDriverInfoAsync(previousDrivers);

        // Act - Remove one, keep one, add one
        var currentDrivers = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 222, DriverName = "Driver 2" },
            new DriverInfo { TransponderId = 333, DriverName = "Driver 3" }
        };

        await InvokeSendDriverInfoAsync(currentDrivers);

        // Assert - Should send kept driver, new driver, and empty entry for removed driver
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(
                It.Is<List<DriverInfo>>(list => 
                    list.Count == 3 &&
                    list.Any(d => d.TransponderId == 222 && d.DriverName == "Driver 2") &&
                    list.Any(d => d.TransponderId == 333 && d.DriverName == "Driver 3") &&
                    list.Any(d => d.TransponderId == 111 && string.IsNullOrEmpty(d.DriverName))),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region SendDriverInfoAsync Tests - Cancellation

    [TestMethod]
    public async Task SendDriverInfoAsync_WithCancellationToken_PassesCancellationToken()
    {
        // Arrange
        var driverInfo = new List<DriverInfo>
        {
            new DriverInfo { TransponderId = 12345, DriverName = "Driver 1" }
        };

        var cts = new CancellationTokenSource();
        
        mockExternalTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), cts.Token))
            .ReturnsAsync(true);

        // Act
        await InvokeSendDriverInfoAsync(driverInfo, cts.Token);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), cts.Token),
            Times.Once);
    }

    #endregion

    #region SendVideoMetadataAsync Tests - Basic Functionality

    [TestMethod]
    public async Task SendVideoMetadataAsync_NewVideos_SendsAllVideos()
    {
        // Arrange
        var videoMetadata = new List<VideoMetadata>
        {
            new VideoMetadata 
            { 
                TransponderId = 12345, 
                SystemType = VideoSystemType.Sentinel,
                Destinations = new List<VideoDestination> 
                { 
                    new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/1" } 
                }
            },
            new VideoMetadata 
            { 
                TransponderId = 67890, 
                SystemType = VideoSystemType.Sentinel,
                Destinations = new List<VideoDestination> 
                { 
                    new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://example.com" } 
                }
            }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await InvokeSendVideoMetadataAsync(videoMetadata);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => list.Count == 2 && 
                    list.Any(v => v.TransponderId == 12345) && 
                    list.Any(v => v.TransponderId == 67890)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendVideoMetadataAsync_EmptyVideoList_SendsEmptyList()
    {
        // Arrange
        var videoMetadata = new List<VideoMetadata>();

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await InvokeSendVideoMetadataAsync(videoMetadata);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => list.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendVideoMetadataAsync_NullLastVideoMetadata_SendsOnlyNewVideos()
    {
        // Arrange - lastVideoMetadata is null by default
        var videoMetadata = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 12345, SystemType = VideoSystemType.Sentinel }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await InvokeSendVideoMetadataAsync(videoMetadata);

        // Assert - Should only send new videos, no removed videos
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => list.Count == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region SendVideoMetadataAsync Tests - Removed Videos

    [TestMethod]
    public async Task SendVideoMetadataAsync_VideosRemoved_SendsEmptyVideoMetadata()
    {
        // Arrange - Set up previous videos
        var previousVideos = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 12345, SystemType = VideoSystemType.Sentinel },
            new VideoMetadata { TransponderId = 67890, SystemType = VideoSystemType.Sentinel }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastVideoMetadata
        await InvokeSendVideoMetadataAsync(previousVideos);

        // Act - Send with one video removed
        var currentVideos = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 12345, SystemType = VideoSystemType.Sentinel }
        };

        await InvokeSendVideoMetadataAsync(currentVideos);

        // Assert - Should send current video plus empty entry for removed video
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => 
                    list.Count == 2 &&
                    list.Any(v => v.TransponderId == 12345 && v.SystemType == VideoSystemType.Sentinel) &&
                    list.Any(v => v.TransponderId == 67890 && v.SystemType == VideoSystemType.None)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendVideoMetadataAsync_AllVideosRemoved_SendsOnlyEmptyEntries()
    {
        // Arrange - Set up previous videos
        var previousVideos = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 12345, SystemType = VideoSystemType.Sentinel },
            new VideoMetadata { TransponderId = 67890, SystemType = VideoSystemType.Sentinel }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastVideoMetadata
        await InvokeSendVideoMetadataAsync(previousVideos);

        // Act - Send with all videos removed
        var currentVideos = new List<VideoMetadata>();

        await InvokeSendVideoMetadataAsync(currentVideos);

        // Assert - Should send only empty entries for removed videos
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => 
                    list.Count == 2 &&
                    list.All(v => v.SystemType == VideoSystemType.None)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendVideoMetadataAsync_MultipleRemovedVideos_SendsAllEmptyEntries()
    {
        // Arrange - Set up previous videos
        var previousVideos = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 111, SystemType = VideoSystemType.Sentinel },
            new VideoMetadata { TransponderId = 222, SystemType = VideoSystemType.Sentinel },
            new VideoMetadata { TransponderId = 333, SystemType = VideoSystemType.Sentinel }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastVideoMetadata
        await InvokeSendVideoMetadataAsync(previousVideos);

        // Act - Send with two videos removed, one remaining
        var currentVideos = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 222, SystemType = VideoSystemType.Sentinel }
        };

        await InvokeSendVideoMetadataAsync(currentVideos);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => 
                    list.Count == 3 &&
                    list.Any(v => v.TransponderId == 222 && v.SystemType == VideoSystemType.Sentinel) &&
                    list.Any(v => v.TransponderId == 111 && v.SystemType == VideoSystemType.None) &&
                    list.Any(v => v.TransponderId == 333 && v.SystemType == VideoSystemType.None)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region SendVideoMetadataAsync Tests - Updates

    [TestMethod]
    public async Task SendVideoMetadataAsync_SameVideosUpdated_SendsUpdatedInfo()
    {
        // Arrange - Set up previous videos
        var previousVideos = new List<VideoMetadata>
        {
            new VideoMetadata 
            { 
                TransponderId = 12345, 
                SystemType = VideoSystemType.Sentinel,
                Destinations = new List<VideoDestination> 
                { 
                    new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://old.com" } 
                }
            }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastVideoMetadata
        await InvokeSendVideoMetadataAsync(previousVideos);

        // Act - Send with same transponder but updated URL
        var currentVideos = new List<VideoMetadata>
        {
            new VideoMetadata 
            { 
                TransponderId = 12345, 
                SystemType = VideoSystemType.Sentinel,
                Destinations = new List<VideoDestination> 
                { 
                    new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://new.com" } 
                }
            }
        };

        await InvokeSendVideoMetadataAsync(currentVideos);

        // Assert - Should send only the updated video
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => 
                    list.Count == 1 &&
                    list.Any(v => v.TransponderId == 12345 && 
                        v.Destinations.Any(d => d.Url == "https://new.com"))),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendVideoMetadataAsync_MixedAdditionsAndRemovals_SendsCorrectList()
    {
        // Arrange - Set up previous videos
        var previousVideos = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 111, SystemType = VideoSystemType.Sentinel },
            new VideoMetadata { TransponderId = 222, SystemType = VideoSystemType.Sentinel }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // First call to set lastVideoMetadata
        await InvokeSendVideoMetadataAsync(previousVideos);

        // Act - Remove one, keep one, add one
        var currentVideos = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 222, SystemType = VideoSystemType.Sentinel },
            new VideoMetadata { TransponderId = 333, SystemType = VideoSystemType.Sentinel }
        };

        await InvokeSendVideoMetadataAsync(currentVideos);

        // Assert - Should send kept video, new video, and empty entry for removed video
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => 
                    list.Count == 3 &&
                    list.Any(v => v.TransponderId == 222 && v.SystemType == VideoSystemType.Sentinel) &&
                    list.Any(v => v.TransponderId == 333 && v.SystemType == VideoSystemType.Sentinel) &&
                    list.Any(v => v.TransponderId == 111 && v.SystemType == VideoSystemType.None)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region SendVideoMetadataAsync Tests - Cancellation

    [TestMethod]
    public async Task SendVideoMetadataAsync_WithCancellationToken_PassesCancellationToken()
    {
        // Arrange
        var videoMetadata = new List<VideoMetadata>
        {
            new VideoMetadata { TransponderId = 12345, SystemType = VideoSystemType.Sentinel }
        };

        var cts = new CancellationTokenSource();
        
        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), cts.Token))
            .ReturnsAsync(true);

        // Act
        await InvokeSendVideoMetadataAsync(videoMetadata, cts.Token);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), cts.Token),
            Times.Once);
    }

    #endregion

    #region SendVideoMetadataAsync Tests - Multiple Destinations

    [TestMethod]
    public async Task SendVideoMetadataAsync_VideoWithMultipleDestinations_SendsAllDestinations()
    {
        // Arrange
        var videoMetadata = new List<VideoMetadata>
        {
            new VideoMetadata 
            { 
                TransponderId = 12345, 
                SystemType = VideoSystemType.Sentinel,
                Destinations = new List<VideoDestination> 
                { 
                    new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com/1" },
                    new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = "srt://example.com" }
                }
            }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await InvokeSendVideoMetadataAsync(videoMetadata);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => 
                    list.Count == 1 &&
                    list[0].Destinations.Count == 2 &&
                    list[0].Destinations.Any(d => d.Type == VideoDestinationType.Youtube) &&
                    list[0].Destinations.Any(d => d.Type == VideoDestinationType.DirectSrt)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task SendVideoMetadataAsync_VideoWithNoDestinations_SendsEmptyDestinationsList()
    {
        // Arrange
        var videoMetadata = new List<VideoMetadata>
        {
            new VideoMetadata 
            { 
                TransponderId = 12345, 
                SystemType = VideoSystemType.Sentinel,
                Destinations = new List<VideoDestination>()
            }
        };

        mockExternalTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await InvokeSendVideoMetadataAsync(videoMetadata);

        // Assert
        mockExternalTelemetryClient.Verify(
            x => x.UpdateCarVideosAsync(
                It.Is<List<VideoMetadata>>(list => 
                    list.Count == 1 &&
                    list[0].Destinations.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Helper method to invoke private SendDriverInfoAsync method using reflection
    /// </summary>
    private async Task InvokeSendDriverInfoAsync(List<DriverInfo> driverInfo, CancellationToken stoppingToken = default)
    {
        var method = typeof(SentinelStatusService).GetMethod("SendDriverInfoAsync", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        Assert.IsNotNull(method, "SendDriverInfoAsync method not found");
        
        var task = method.Invoke(sentinelStatusService, new object[] { driverInfo, stoppingToken }) as Task;
        
        Assert.IsNotNull(task, "Method invocation returned null");
        
        await task;
        
        // Simulate what ExecuteAsync does - update lastDriverInfo after sending
        var lastDriverInfoField = typeof(SentinelStatusService).GetField("lastDriverInfo", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(lastDriverInfoField, "lastDriverInfo field not found");
        lastDriverInfoField.SetValue(sentinelStatusService, driverInfo);
    }

    /// <summary>
    /// Helper method to invoke private SendVideoMetadataAsync method using reflection
    /// </summary>
    private async Task InvokeSendVideoMetadataAsync(List<VideoMetadata> metadata, CancellationToken stoppingToken = default)
    {
        // Get the non-obsolete overload (the one with 2 parameters)
        var method = typeof(SentinelStatusService).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "SendVideoMetadataAsync" && 
                m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType == typeof(List<VideoMetadata>) &&
                m.GetParameters()[1].ParameterType == typeof(CancellationToken));
        
        Assert.IsNotNull(method, "SendVideoMetadataAsync method not found");
        
        var task = method.Invoke(sentinelStatusService, new object[] { metadata, stoppingToken }) as Task;
        
        Assert.IsNotNull(task, "Method invocation returned null");
        
        await task;
        
        // Simulate what ExecuteAsync does - update lastVideoMetadata after sending
        var lastVideoMetadataField = typeof(SentinelStatusService).GetField("lastVideoMetadata", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(lastVideoMetadataField, "lastVideoMetadata field not found");
        lastVideoMetadataField.SetValue(sentinelStatusService, metadata);
    }

    #endregion
}
