using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.DriverInformation;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;
using DriverInfo = RedMist.TimingCommon.Models.DriverInfo;

namespace RedMist.EventProcessor.Tests.EventStatus.DriverInformation;

[TestClass]
public class DriverEnricherTests
{
    private Mock<ILoggerFactory> mockLoggerFactory = null!;
    private Mock<ILogger> mockLogger = null!;
    private Mock<IConnectionMultiplexer> mockConnectionMultiplexer = null!;
    private Mock<IDatabase> mockDatabase = null!;
    private SessionContext sessionContext = null!;
    private DriverEnricher driverEnricher = null!;

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

        driverEnricher = new DriverEnricher(sessionContext, mockLoggerFactory.Object, mockConnectionMultiplexer.Object);
    }

    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidParameters_InitializesCorrectly()
    {
        // Arrange & Act - Constructor called in Setup

        // Assert
        Assert.IsNotNull(driverEnricher);
        mockLoggerFactory.Verify(x => x.CreateLogger(It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region Process Method Tests - Car Number Matching

    [TestMethod]
    public void Process_ValidDriverInfoWithCarNumber_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345, DriverId = "old-id", DriverName = "Old Name" };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            CarNumber = "42",
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var message = new TimingMessage(
            Consts.DRIVER_EVENT_TYPE,
            JsonSerializer.Serialize(driverInfo),
            1,
            DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CarPatches.Count);
        
        // Verify car position was updated
        Assert.AreEqual("driver-123", car.DriverId);
        Assert.AreEqual("John Doe", car.DriverName);
    }

    [TestMethod]
    public void Process_CarNumberNotFound_ReturnsNull()
    {
        // Arrange
        var driverInfo = new DriverInfo
        {
            CarNumber = "99",
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var message = new TimingMessage(
            Consts.DRIVER_EVENT_TYPE,
            JsonSerializer.Serialize(driverInfo),
            1,
            DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Process_EmptyCarNumber_SkipsCarNumberLookup()
    {
        // Arrange
        var driverInfo = new DriverInfo
        {
            CarNumber = "",
            TransponderId = 0,
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var message = new TimingMessage(
            Consts.DRIVER_EVENT_TYPE,
            JsonSerializer.Serialize(driverInfo),
            1,
            DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region Process Method Tests - Transponder Matching

    [TestMethod]
    public void Process_ValidDriverInfoWithTransponderId_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345, DriverId = "old-id", DriverName = "Old Name" };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            CarNumber = "",
            TransponderId = 12345,
            DriverId = "driver-456",
            DriverName = "Jane Smith"
        };

        var message = new TimingMessage(
            Consts.DRIVER_EVENT_TYPE,
            JsonSerializer.Serialize(driverInfo),
            1,
            DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CarPatches.Count);
        
        // Verify car position was updated
        Assert.AreEqual("driver-456", car.DriverId);
        Assert.AreEqual("Jane Smith", car.DriverName);
    }

    [TestMethod]
    public void Process_TransponderNotFound_ReturnsNull()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            CarNumber = "",
            TransponderId = 99999, // Different transponder
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var message = new TimingMessage(
            Consts.DRIVER_EVENT_TYPE,
            JsonSerializer.Serialize(driverInfo),
            1,
            DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Process_ZeroTransponderId_ReturnsNull()
    {
        // Arrange
        var driverInfo = new DriverInfo
        {
            CarNumber = "",
            TransponderId = 0,
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var message = new TimingMessage(
            Consts.DRIVER_EVENT_TYPE,
            JsonSerializer.Serialize(driverInfo),
            1,
            DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

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

        var driverInfo = new DriverInfo
        {
            CarNumber = "42",
            TransponderId = 67890, // Different transponder
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var message = new TimingMessage(
            Consts.DRIVER_EVENT_TYPE,
            JsonSerializer.Serialize(driverInfo),
            1,
            DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CarPatches.Count);
        
        // Should match car number, not transponder
        Assert.AreEqual("driver-123", car1.DriverId);
        Assert.AreEqual("John Doe", car1.DriverName);
        
        // Car 2 should remain unchanged
        Assert.IsTrue(string.IsNullOrEmpty(car2.DriverId));
    }

    #endregion

    #region Process Method Tests - Invalid/Malformed Data

    [TestMethod]
    public void Process_NullMessageData_LogsWarning()
    {
        // Arrange
        var message = new TimingMessage(Consts.DRIVER_EVENT_TYPE, null!, 1, DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Unable to deserialize DriverInfo");
    }

    [TestMethod]
    public void Process_InvalidJsonData_LogsWarning()
    {
        // Arrange
        var message = new TimingMessage(Consts.DRIVER_EVENT_TYPE, "invalid json", 1, DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Unable to deserialize DriverInfo");
    }

    [TestMethod]
    public void Process_EmptyJsonObject_ReturnsNull()
    {
        // Arrange
        var message = new TimingMessage(Consts.DRIVER_EVENT_TYPE, "{}", 1, DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region Process Method Tests - No Changes

    [TestMethod]
    public void Process_SameDriverInfo_ReturnsNull()
    {
        // Arrange
        var car = new CarPosition 
        { 
            Number = "42", 
            TransponderId = 12345,
            DriverId = "driver-123",
            DriverName = "John Doe"
        };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            CarNumber = "42",
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var message = new TimingMessage(
            Consts.DRIVER_EVENT_TYPE,
            JsonSerializer.Serialize(driverInfo),
            1,
            DateTime.UtcNow);

        // Act
        var result = driverEnricher.Process(message);

        // Assert
        Assert.IsNull(result); // No changes, so no patch
    }

    #endregion

    #region ProcessApplyFullAsync Tests - Cache Integration

    [TestMethod]
    public async Task ProcessApplyFullAsync_CarWithDriverInfoInCache_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            CarNumber = "42",
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var key = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "42");
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(driverInfo));

        // Act
        var result = await driverEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CarPatches.Count);
        
        // Verify car position was updated
        Assert.AreEqual("driver-123", car.DriverId);
        Assert.AreEqual("John Doe", car.DriverName);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_CarWithTransponderIdFallback_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            TransponderId = 12345,
            DriverId = "driver-456",
            DriverName = "Jane Smith"
        };

        // First key (event + car number) returns no value
        var key1 = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "42");
        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Second key (transponder only) returns metadata
        var key2 = string.Format(Consts.DRIVER_TRANSPONDER_KEY, 12345);
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(driverInfo));

        // Act
        var result = await driverEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CarPatches.Count);
        
        // Verify car position was updated
        Assert.AreEqual("driver-456", car.DriverId);
        Assert.AreEqual("Jane Smith", car.DriverName);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_NoDriverInfoInCache_ClearsExistingInfo()
    {
        // Arrange
        var car = new CarPosition 
        { 
            Number = "42", 
            TransponderId = 12345,
            DriverId = "old-driver",
            DriverName = "Old Driver"
        };
        sessionContext.UpdateCars([car]);

        // All cache keys return null
        mockDatabase.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await driverEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CarPatches.Count);
        
        var patch = result.CarPatches[0];
        Assert.AreEqual(string.Empty, patch.DriverId);
        Assert.AreEqual(string.Empty, patch.DriverName);

        // Verify car position was cleared
        Assert.AreEqual(string.Empty, car.DriverId);
        Assert.AreEqual(string.Empty, car.DriverName);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_MultipleCars_UpdatesAllCars()
    {
        // Arrange
        var car1 = new CarPosition { Number = "1", TransponderId = 111 };
        var car2 = new CarPosition { Number = "2", TransponderId = 222 };
        sessionContext.UpdateCars([car1, car2]);

        var info1 = new DriverInfo
        {
            CarNumber = "1",
            DriverId = "driver-1",
            DriverName = "Driver One"
        };

        var info2 = new DriverInfo
        {
            CarNumber = "2",
            DriverId = "driver-2",
            DriverName = "Driver Two"
        };

        var key1 = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "1");
        var key2 = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "2");

        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(info1));
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(info2));

        // Act
        var result = await driverEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.CarPatches.Count);
        
        Assert.AreEqual("driver-1", car1.DriverId);
        Assert.AreEqual("Driver One", car1.DriverName);
        
        Assert.AreEqual("driver-2", car2.DriverId);
        Assert.AreEqual("Driver Two", car2.DriverName);
    }

    [TestMethod]
    public async Task ProcessApplyFullAsync_NoCars_ReturnsNull()
    {
        // Arrange
        sessionContext.SessionState.CarPositions.Clear();

        // Act
        var result = await driverEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region ProcessApplyFullAsync Tests - Error Handling

    [TestMethod]
    public async Task ProcessApplyFullAsync_InvalidJsonInCache_ClearsDriverInfo()
    {
        // Arrange
        var car = new CarPosition 
        { 
            Number = "42", 
            TransponderId = 12345,
            DriverId = "old-driver",
            DriverName = "Old Driver"
        };
        sessionContext.UpdateCars([car]);

        var key = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "42");
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)"invalid json");

        // Setup remaining keys to return null
        mockDatabase.Setup(x => x.StringGetAsync(
            It.Is<RedisKey>(k => k.ToString() != key.ToString()), 
            CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await driverEnricher.ProcessApplyFullAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.CarPatches.Count);
        
        // Verify warning was logged
        VerifyLogWarning("Unable to deserialize DriverInfo");
        
        // Verify driver info was cleared
        Assert.AreEqual(string.Empty, car.DriverId);
        Assert.AreEqual(string.Empty, car.DriverName);
    }

    #endregion

    #region ProcessCarAsync Tests - Basic Functionality

    [TestMethod]
    public async Task ProcessCarAsync_ValidCarNumberWithCache_UpdatesCarPosition()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            CarNumber = "42",
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var key = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "42");
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(driverInfo));

        // Act
        var result = await driverEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("driver-123", car.DriverId);
        Assert.AreEqual("John Doe", car.DriverName);
    }

    [TestMethod]
    public async Task ProcessCarAsync_ValidCarNumberWithoutExplicitCache_UsesDefaultCache()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            CarNumber = "42",
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var key = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "42");
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(driverInfo));

        // Act - Call without explicit cache parameter
        var result = await driverEnricher.ProcessCarAsync("42");

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("driver-123", car.DriverId);

        // Verify the mock database was accessed via IConnectionMultiplexer
        mockConnectionMultiplexer.Verify(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task ProcessCarAsync_NullCarNumber_ReturnsNullAndLogsWarning()
    {
        // Act
        var result = await driverEnricher.ProcessCarAsync(null!);

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Car number is null or empty in ProcessCarAsync");
    }

    [TestMethod]
    public async Task ProcessCarAsync_EmptyCarNumber_ReturnsNullAndLogsWarning()
    {
        // Act
        var result = await driverEnricher.ProcessCarAsync("");

        // Assert
        Assert.IsNull(result);
        VerifyLogWarning("Car number is null or empty in ProcessCarAsync");
    }

    [TestMethod]
    public async Task ProcessCarAsync_CarNotFound_ReturnsNullAndLogsWarning()
    {
        // Arrange - No cars in session context

        // Act
        var result = await driverEnricher.ProcessCarAsync("99");

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

        var driverInfo = new DriverInfo
        {
            TransponderId = 12345,
            DriverId = "driver-456",
            DriverName = "Jane Smith"
        };

        // First key (event + car number) returns no value
        var key1 = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "42");
        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Second key (transponder only) returns metadata
        var key2 = string.Format(Consts.DRIVER_TRANSPONDER_KEY, 12345);
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(driverInfo));

        // Act
        var result = await driverEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("driver-456", car.DriverId);
        Assert.AreEqual("Jane Smith", car.DriverName);
    }

    [TestMethod]
    public async Task ProcessCarAsync_ZeroTransponderId_SkipsTransponderLookup()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 0 };
        sessionContext.UpdateCars([car]);

        var driverInfo = new DriverInfo
        {
            CarNumber = "42",
            DriverId = "driver-123",
            DriverName = "John Doe"
        };

        var key = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "42");
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(driverInfo));

        // Act
        var result = await driverEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("driver-123", car.DriverId);
        
        // Verify only the first key was checked
        mockDatabase.Verify(x => x.StringGetAsync(key, CommandFlags.None), Times.Once);
    }

    #endregion

    #region ProcessCarAsync Tests - Edge Cases

    [TestMethod]
    public async Task ProcessCarAsync_MultipleCallsForSameCar_UpdatesCorrectly()
    {
        // Arrange
        var car = new CarPosition { Number = "42", TransponderId = 12345 };
        sessionContext.UpdateCars([car]);

        var info1 = new DriverInfo
        {
            CarNumber = "42",
            DriverId = "driver-1",
            DriverName = "First Driver"
        };

        var key = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "42");
        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(info1));

        // Act - First call
        var result1 = await driverEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert first call
        Assert.IsNotNull(result1);
        Assert.AreEqual("driver-1", car.DriverId);
        Assert.AreEqual("First Driver", car.DriverName);

        // Arrange second call with different info
        var info2 = new DriverInfo
        {
            CarNumber = "42",
            DriverId = "driver-2",
            DriverName = "Second Driver"
        };

        mockDatabase.Setup(x => x.StringGetAsync(key, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(info2));

        // Act - Second call
        var result2 = await driverEnricher.ProcessCarAsync("42", mockDatabase.Object);

        // Assert second call
        Assert.IsNotNull(result2);
        Assert.AreEqual("driver-2", car.DriverId);
        Assert.AreEqual("Second Driver", car.DriverName);
    }

    [TestMethod]
    public async Task ProcessCarAsync_DifferentCarsSequentially_UpdatesEachIndependently()
    {
        // Arrange
        var car1 = new CarPosition { Number = "1", TransponderId = 111 };
        var car2 = new CarPosition { Number = "2", TransponderId = 222 };
        sessionContext.UpdateCars([car1, car2]);

        var info1 = new DriverInfo
        {
            CarNumber = "1",
            DriverId = "driver-1",
            DriverName = "Driver One"
        };

        var info2 = new DriverInfo
        {
            CarNumber = "2",
            DriverId = "driver-2",
            DriverName = "Driver Two"
        };

        var key1 = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "1");
        var key2 = string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "2");

        mockDatabase.Setup(x => x.StringGetAsync(key1, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(info1));
        mockDatabase.Setup(x => x.StringGetAsync(key2, CommandFlags.None))
            .ReturnsAsync((RedisValue)JsonSerializer.Serialize(info2));

        // Act
        var result1 = await driverEnricher.ProcessCarAsync("1", mockDatabase.Object);
        var result2 = await driverEnricher.ProcessCarAsync("2", mockDatabase.Object);

        // Assert
        Assert.IsNotNull(result1);
        Assert.AreEqual("driver-1", car1.DriverId);
        Assert.AreEqual("Driver One", car1.DriverName);

        Assert.IsNotNull(result2);
        Assert.AreEqual("driver-2", car2.DriverId);
        Assert.AreEqual("Driver Two", car2.DriverName);
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
