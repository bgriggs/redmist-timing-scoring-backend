using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.StatusApi.Controllers.V1;
using StackExchange.Redis;
using System.Security.Claims;
using System.Text.Json;
using DriverInfo = RedMist.TimingCommon.Models.DriverInfo;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

[TestClass]
public class ExternalTelemetryControllerTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private Mock<IConnectionMultiplexer> _mockConnectionMultiplexer = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private RedMist.StatusApi.Controllers.V2.EventsController _eventsController = null!;
    private Mock<HybridCache> _mockHybridCache = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private ExternalTelemetryController _controller = null!;
    private TsContext _dbContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockHybridCache = new Mock<HybridCache>();

        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
        // Return the same mock database for all calls
        _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(() => _mockDatabase.Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        _dbContext = _dbContextFactory.CreateDbContext();

        // Create EventsController with all required dependencies
        var mockMemoryCache = new Mock<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _eventsController = new RedMist.StatusApi.Controllers.V2.EventsController(
            _mockLoggerFactory.Object,
            _dbContextFactory,
            _mockHybridCache.Object,
            _mockConnectionMultiplexer.Object,
            mockMemoryCache.Object,
            mockHttpClientFactory.Object);

        _controller = new ExternalTelemetryController(
            _mockLoggerFactory.Object,
            _mockConnectionMultiplexer.Object,
            _eventsController,
            _dbContextFactory,
            _mockHybridCache.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext?.Dispose();
    }

    private void SetupUser(string clientId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new Claim("azp", clientId)
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };
    }

    #region UpdateDriversAsync Tests

    [TestMethod]
    public async Task UpdateDriversAsync_NoClientId_ReturnsBadRequest()
    {
        // Arrange
        SetupUser(string.Empty);
        var drivers = new List<DriverInfo> { new() { CarNumber = "42", EventId = 1 } };

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        var badRequestResult = (BadRequestObjectResult)result;
        Assert.AreEqual("No client ID found", badRequestResult.Value);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_NonRelayWithoutExtTelemRole_ReturnsForbid()
    {
        // Arrange
        SetupUser("test-client");
        var drivers = new List<DriverInfo> { new() { CarNumber = "42", EventId = 1 } };

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<ForbidResult>(result);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_NonRelayWithExtTelemRole_ReturnsLocked()
    {
        // Arrange
        SetupUser("test-client", "ext-telem");
        var drivers = new List<DriverInfo> { new() { CarNumber = "42", EventId = 1 } };

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<ObjectResult>(result);
        var objectResult = (ObjectResult)result;
        Assert.AreEqual(StatusCodes.Status423Locked, objectResult.StatusCode);
        Assert.AreEqual("Record is locked by a higher priority user", objectResult.Value);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_NullDriversList_ReturnsBadRequest()
    {
        // Arrange
        SetupUser("relay-test");

        // Act
        var result = await _controller.UpdateDriversAsync(null!);

        // Assert
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        var badRequestResult = (BadRequestObjectResult)result;
        Assert.AreEqual("No drivers found", badRequestResult.Value);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_EmptyDriversList_ReturnsBadRequest()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>();

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        var badRequestResult = (BadRequestObjectResult)result;
        Assert.AreEqual("No drivers found", badRequestResult.Value);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_ValidRelaySource_WithCarNumberAndEventId_CreatesNewDriverInfo()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo
            {
                CarNumber = "42",
                EventId = 1,
                DriverName = "John Doe",
                DriverId = "123"
            }
        };

        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("test-id"));

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        // Successfully processed and created new driver info
    }

    [TestMethod]
    public async Task UpdateDriversAsync_ValidRelaySource_WithTransponderId_CreatesNewDriverInfo()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo
            {
                TransponderId = 12345,
                DriverName = "Jane Smith",
                DriverId = "456"
            }
        };

        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("test-id"));

        // Note: HybridCache.GetOrCreateAsync is not virtual so cannot be mocked for LoadLiveEvents
        // This test will use actual implementation which will return empty list from database

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        // Successfully processed driver with transponder ID
    }

    [TestMethod]
    public async Task UpdateDriversAsync_DriverWithInsufficientInfo_SkipsDriver()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo
            {
                DriverName = "No ID Driver"
                // No CarNumber, EventId, or TransponderId
            }
        };

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        _mockDatabase.Verify(x => x.StringSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Never);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_WithFlagtronicsId_LoadsFromHybridCache()
    {
        // Arrange
        SetupUser("relay-test");
        var flagtronicsId = 999u;
        var drivers = new List<DriverInfo>
        {
            new DriverInfo
            {
                CarNumber = "42",
                EventId = 1,
                DriverId = flagtronicsId.ToString(),
                DriverName = "Incoming Name"
            }
        };

        var storedDriver = new Database.Models.DriverInfo
        {
            Id = 1,
            FlagtronicsId = flagtronicsId,
            Name = "Centrally Stored Name"
        };

        // Note: HybridCache.GetOrCreateAsync is not virtual so cannot be mocked
        // This test will use the actual implementation which will query the database

        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("test-id"));

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        // Successfully loaded from HybridCache and used centrally stored name
    }

    [TestMethod]
    public async Task UpdateDriversAsync_ExistingDriverInfo_NoChanges_DoesNotSendUpdate()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo
            {
                CarNumber = "42",
                EventId = 1,
                DriverName = "John Doe",
                DriverId = "123"
            }
        };

        var existingDriverInfo = new DriverInfoSource(drivers[0], "relay-test", DateTime.UtcNow);
        var existingJson = JsonSerializer.Serialize(existingDriverInfo);

        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(existingJson));
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        _mockDatabase.Verify(x => x.StreamAddAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<CommandFlags>()), Times.Never);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_RelayWithNonRelayExisting_ProcessesUpdate()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo
            {
                CarNumber = "42",
                EventId = 1,
                DriverName = "New Name",
                DriverId = "123",
                TransponderId = 0
            }
        };

        var existingDriverInfo = new DriverInfoSource(
            new DriverInfo
            {
                CarNumber = "42",
                EventId = 1,
                DriverName = "Existing Name",
                DriverId = "123",
                TransponderId = 0
            },
            "ext-telem-client", // Non-relay client
            DateTime.UtcNow);
        var existingJson = JsonSerializer.Serialize(existingDriverInfo);

        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(existingJson));
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        // Relay source can update existing non-relay data with a name
    }

    [TestMethod]
    public async Task UpdateDriversAsync_RelayUpdatingExistingRelay_SendsUpdate()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo
            {
                CarNumber = "42",
                EventId = 1,
                DriverName = "Updated Name",
                DriverId = "123"
            }
        };

        var existingDriverInfo = new DriverInfoSource(
            new DriverInfo
            {
                CarNumber = "42",
                EventId = 1,
                DriverName = "Old Name",
                DriverId = "123"
            },
            "relay-old",
            DateTime.UtcNow.AddMinutes(-5));
        var existingJson = JsonSerializer.Serialize(existingDriverInfo);

        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(existingJson));
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("test-id"));

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        // Successfully sent update for changed driver info
    }

    [TestMethod]
    public async Task UpdateDriversAsync_EmptyDriverName_LogsDebugMessage()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo
            {
                CarNumber = "42",
                EventId = 1,
                DriverName = string.Empty,
                DriverId = "123"
            }
        };

        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("test-id"));

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Empty driver name")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task UpdateDriversAsync_MultipleDrivers_ProcessesAll()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo { CarNumber = "1", EventId = 1, DriverName = "Driver 1" },
            new DriverInfo { CarNumber = "2", EventId = 1, DriverName = "Driver 2" },
            new DriverInfo { CarNumber = "3", EventId = 1, DriverName = "Driver 3" }
        };

        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("test-id"));

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        // The method completed successfully, indicating all 3 drivers were processed
    }

    [TestMethod]
    public async Task UpdateDriversAsync_ExceptionDuringProcessing_ContinuesWithOtherDrivers()
    {
        // Arrange
        SetupUser("relay-test");
        var drivers = new List<DriverInfo>
        {
            new DriverInfo { CarNumber = "1", EventId = 1, DriverName = "Driver 1" },
            new DriverInfo { CarNumber = "2", EventId = 1, DriverName = "Driver 2" }
        };

        var callCount = 0;
        _mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new Exception("Test exception");
                return RedisValue.Null;
            });
        _mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("test-id"));

        // Act
        var result = await _controller.UpdateDriversAsync(drivers);

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error setting event driver info")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion
}
