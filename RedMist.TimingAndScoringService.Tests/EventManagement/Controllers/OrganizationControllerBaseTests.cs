using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Controllers;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Security.Claims;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;

namespace RedMist.TimingAndScoringService.Tests.EventManagement.Controllers;

[TestClass]
public class OrganizationControllerBaseTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private Mock<IControlLogFactory> _mockControlLogFactory = null!;
    private Mock<IControlLog> _mockControlLog = null!;
    private Mock<AssetsCdn> _mockAssetsCdn = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private TestOrganizationController _controller = null!;
    private TsContext _dbContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockControlLogFactory = new Mock<IControlLogFactory>();
        _mockControlLog = new Mock<IControlLog>();

        var mockConfiguration = new Mock<IConfiguration>();
        mockConfiguration.Setup(c => c["Assets:StorageZoneName"]).Returns("test-zone");
        mockConfiguration.Setup(c => c["Assets:StorageAccessKey"]).Returns("test-key");
        mockConfiguration.Setup(c => c["Assets:MainReplicationRegion"]).Returns("test-region");
        mockConfiguration.Setup(c => c["Assets:ApiAccessKey"]).Returns("test-api-key");
        mockConfiguration.Setup(c => c["Assets:CdnId"]).Returns("test-cdn-id");

        _mockAssetsCdn = new Mock<AssetsCdn>(mockConfiguration.Object, _mockLoggerFactory.Object);

        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        _dbContext = _dbContextFactory.CreateDbContext();

        _controller = new TestOrganizationController(
            _mockLoggerFactory.Object,
            _dbContextFactory,
            _mockControlLogFactory.Object,
            _mockAssetsCdn.Object);

        SetupDefaultUser();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext?.Dispose();
    }

    private void SetupDefaultUser()
    {
        var claims = new List<Claim>
        {
            new Claim("client_id", "test-client-id")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };
    }

    #region GetControlLogStatistics Tests

    [TestMethod]
    public async Task GetControlLogStatistics_EmptyControlLogType_ReturnsDefaultStatistics()
    {
        // Arrange
        var organization = new Organization { Id = 1, ControlLogType = string.Empty };

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsConnected);
        Assert.AreEqual(0, result.TotalEntries);
        Assert.IsFalse(result.IsStaleWarning);
    }

    [TestMethod]
    public async Task GetControlLogStatistics_NullControlLogType_ReturnsDefaultStatistics()
    {
        // Arrange
        var organization = new Organization { Id = 1, ControlLogType = null! };

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsConnected);
        Assert.AreEqual(0, result.TotalEntries);
        Assert.IsFalse(result.IsStaleWarning);
    }

    [TestMethod]
    public async Task GetControlLogStatistics_SuccessfulConnection_ReturnsCorrectStatistics()
    {
        // Arrange
        var organization = new Organization { Id = 1, ControlLogType = "TestType", ControlLogParams = "test-params" };
        var controlLogEntries = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Timestamp = DateTime.Now },
            new ControlLogEntry { OrderId = 2, Car1 = "2", Timestamp = DateTime.Now.AddMinutes(1) }
        };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, controlLogEntries.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsConnected);
        Assert.AreEqual(2, result.TotalEntries);
        _mockControlLogFactory.Verify(x => x.CreateControlLog("TestType"), Times.Once);
        _mockControlLog.Verify(x => x.LoadControlLogAsync("test-params", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetControlLogStatistics_FailedConnection_ReturnsFailedStatistics()
    {
        // Arrange
        var organization = new Organization { Id = 1, ControlLogType = "TestType", ControlLogParams = "test-params" };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, Enumerable.Empty<ControlLogEntry>()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsConnected);
        Assert.AreEqual(0, result.TotalEntries);
    }

    [TestMethod]
    public async Task GetControlLogStatistics_WithHistoricalData_SetsStaleWarning()
    {
        // Arrange
        var orgId = 1;
        var eventId = 10;

        // Create organization and event
        var organization = new Organization { Id = orgId, ClientId = "test-client-id", ControlLogType = "TestType", ControlLogParams = "test-params" };
        var evt = new ConfigEvent { Id = eventId, OrganizationId = orgId, Name = "Test Event" };
        _dbContext.Organizations.Add(organization);
        _dbContext.Events.Add(evt);

        // Use fixed timestamps for comparison
        var timestamp1 = new DateTime(2024, 1, 1, 10, 0, 0);
        var timestamp2 = new DateTime(2024, 1, 1, 10, 1, 0);

        // Create historical session with similar control logs
        var historicalLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Timestamp = timestamp1, Status = "Warning", Corner = "T1" },
            new ControlLogEntry { OrderId = 2, Car1 = "2", Timestamp = timestamp2, Status = "Penalty", Corner = "T2" }
        };
        var session = new SessionResult
        {
            EventId = eventId,
            SessionId = 1,
            Start = DateTime.Now.AddDays(-1),
            ControlLogs = historicalLogs
        };
        _dbContext.SessionResults.Add(session);
        await _dbContext.SaveChangesAsync();

        // Setup current control logs that are identical (same timestamps for comparison)
        var currentLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Timestamp = timestamp1, Status = "Warning", Corner = "T1" },
            new ControlLogEntry { OrderId = 2, Car1 = "2", Timestamp = timestamp2, Status = "Penalty", Corner = "T2" }
        };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, currentLogs.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsConnected);
        Assert.AreEqual(2, result.TotalEntries);
        Assert.IsTrue(result.IsStaleWarning); // Should detect similarity
    }

    #endregion

    #region DetermineControlLogStaleAsync Tests (via public method)

    [TestMethod]
    public async Task DetermineControlLogStale_EmptyCurrentLog_ReturnsFalse()
    {
        // Arrange
        var organization = new Organization { Id = 1, ControlLogType = "TestType", ControlLogParams = "test-params" };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, Enumerable.Empty<ControlLogEntry>()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsFalse(result.IsStaleWarning);
    }

    [TestMethod]
    public async Task DetermineControlLogStale_NoHistoricalSessions_ReturnsFalse()
    {
        // Arrange
        var orgId = 1;
        var organization = new Organization { Id = orgId, ClientId = "test-client-id", ControlLogType = "TestType", ControlLogParams = "test-params" };
        _dbContext.Organizations.Add(organization);
        await _dbContext.SaveChangesAsync();

        var currentLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Timestamp = DateTime.Now }
        };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, currentLogs.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsFalse(result.IsStaleWarning);
    }

    [TestMethod]
    public async Task DetermineControlLogStale_LowSimilarity_ReturnsFalse()
    {
        // Arrange
        var orgId = 1;
        var eventId = 10;

        var organization = new Organization { Id = orgId, ClientId = "test-client-id", ControlLogType = "TestType", ControlLogParams = "test-params" };
        var evt = new ConfigEvent { Id = eventId, OrganizationId = orgId, Name = "Test Event" };
        _dbContext.Organizations.Add(organization);
        _dbContext.Events.Add(evt);

        // Create historical session with different control logs
        var historicalLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "99", Timestamp = DateTime.Now.AddDays(-1), Status = "DifferentStatus", Corner = "T10" },
            new ControlLogEntry { OrderId = 2, Car1 = "88", Timestamp = DateTime.Now.AddDays(-1).AddMinutes(1), Status = "AnotherStatus", Corner = "T11" }
        };
        var session = new SessionResult
        {
            EventId = eventId,
            SessionId = 1,
            Start = DateTime.Now.AddDays(-1),
            ControlLogs = historicalLogs
        };
        _dbContext.SessionResults.Add(session);
        await _dbContext.SaveChangesAsync();

        // Setup current control logs that are completely different
        var currentLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 3, Car1 = "1", Timestamp = DateTime.Now, Status = "Warning", Corner = "T1" },
            new ControlLogEntry { OrderId = 4, Car1 = "2", Timestamp = DateTime.Now.AddMinutes(1), Status = "Penalty", Corner = "T2" }
        };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, currentLogs.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsFalse(result.IsStaleWarning); // Should not detect similarity
    }

    [TestMethod]
    public async Task DetermineControlLogStale_HighSimilarity_ReturnsTrue()
    {
        // Arrange
        var orgId = 1;
        var eventId = 10;

        var organization = new Organization { Id = orgId, ClientId = "test-client-id", ControlLogType = "TestType", ControlLogParams = "test-params" };
        var evt = new ConfigEvent { Id = eventId, OrganizationId = orgId, Name = "Test Event" };
        _dbContext.Organizations.Add(organization);
        _dbContext.Events.Add(evt);

        // Create historical session with similar control logs
        var timestamp = DateTime.Now.AddDays(-1);
        var historicalLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Car2 = string.Empty, Timestamp = timestamp, Status = "Warning", Corner = "T1", Note = "Test", OtherNotes = "Notes" },
            new ControlLogEntry { OrderId = 2, Car1 = "2", Car2 = string.Empty, Timestamp = timestamp.AddMinutes(1), Status = "Penalty", Corner = "T2", Note = "Test2", OtherNotes = "Notes2" }
        };
        var session = new SessionResult
        {
            EventId = eventId,
            SessionId = 1,
            Start = DateTime.Now.AddDays(-1),
            ControlLogs = historicalLogs
        };
        _dbContext.SessionResults.Add(session);
        await _dbContext.SaveChangesAsync();

        // Setup current control logs that are identical (100% similarity)
        var currentLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Car2 = string.Empty, Timestamp = timestamp, Status = "Warning", Corner = "T1", Note = "Test", OtherNotes = "Notes" },
            new ControlLogEntry { OrderId = 2, Car1 = "2", Car2 = string.Empty, Timestamp = timestamp.AddMinutes(1), Status = "Penalty", Corner = "T2", Note = "Test2", OtherNotes = "Notes2" }
        };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, currentLogs.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsTrue(result.IsStaleWarning); // Should detect 100% similarity
    }

    [TestMethod]
    public async Task DetermineControlLogStale_ExactlyFiftyPercentSimilarity_ReturnsTrue()
    {
        // Arrange
        var orgId = 1;
        var eventId = 10;

        var organization = new Organization { Id = orgId, ClientId = "test-client-id", ControlLogType = "TestType", ControlLogParams = "test-params" };
        var evt = new ConfigEvent { Id = eventId, OrganizationId = orgId, Name = "Test Event" };
        _dbContext.Organizations.Add(organization);
        _dbContext.Events.Add(evt);

        // Create historical session - 4 entries
        var timestamp = DateTime.Now.AddDays(-1);
        var historicalLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Car2 = string.Empty, Timestamp = timestamp, Status = "Warning", Corner = "T1", Note = "A", OtherNotes = "N1" },
            new ControlLogEntry { OrderId = 2, Car1 = "2", Car2 = string.Empty, Timestamp = timestamp.AddMinutes(1), Status = "Penalty", Corner = "T2", Note = "B", OtherNotes = "N2" },
            new ControlLogEntry { OrderId = 3, Car1 = "3", Car2 = string.Empty, Timestamp = timestamp.AddMinutes(2), Status = "Different", Corner = "T3", Note = "C", OtherNotes = "N3" },
            new ControlLogEntry { OrderId = 4, Car1 = "4", Car2 = string.Empty, Timestamp = timestamp.AddMinutes(3), Status = "Different", Corner = "T4", Note = "D", OtherNotes = "N4" }
        };
        var session = new SessionResult
        {
            EventId = eventId,
            SessionId = 1,
            Start = DateTime.Now.AddDays(-1),
            ControlLogs = historicalLogs
        };
        _dbContext.SessionResults.Add(session);
        await _dbContext.SaveChangesAsync();

        // Current logs match first 2 out of 4 (50% similarity)
        var currentLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Car2 = string.Empty, Timestamp = timestamp, Status = "Warning", Corner = "T1", Note = "A", OtherNotes = "N1" },
            new ControlLogEntry { OrderId = 2, Car1 = "2", Car2 = string.Empty, Timestamp = timestamp.AddMinutes(1), Status = "Penalty", Corner = "T2", Note = "B", OtherNotes = "N2" },
            new ControlLogEntry { OrderId = 99, Car1 = "99", Car2 = string.Empty, Timestamp = timestamp.AddMinutes(2), Status = "New", Corner = "T99", Note = "X", OtherNotes = "NX" },
            new ControlLogEntry { OrderId = 100, Car1 = "100", Car2 = string.Empty, Timestamp = timestamp.AddMinutes(3), Status = "New", Corner = "T100", Note = "Y", OtherNotes = "NY" }
        };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, currentLogs.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsTrue(result.IsStaleWarning); // 50% is >= threshold
    }

    [TestMethod]
    public async Task DetermineControlLogStale_MultipleEvents_ChecksOnlyOrgEvents()
    {
        // Arrange
        var orgId = 1;
        var otherOrgId = 999;
        var eventId = 10;
        var otherEventId = 20;

        var organization = new Organization { Id = orgId, ClientId = "test-client-id", ControlLogType = "TestType", ControlLogParams = "test-params" };
        var evt = new ConfigEvent { Id = eventId, OrganizationId = orgId, Name = "Test Event" };
        var otherEvt = new ConfigEvent { Id = otherEventId, OrganizationId = otherOrgId, Name = "Other Org Event" };
        _dbContext.Organizations.Add(organization);
        _dbContext.Events.Add(evt);
        _dbContext.Events.Add(otherEvt);

        // Create session for different org (should be ignored)
        var otherOrgLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Timestamp = DateTime.Now.AddDays(-1), Status = "Warning", Corner = "T1" }
        };
        var otherSession = new SessionResult
        {
            EventId = otherEventId,
            SessionId = 1,
            Start = DateTime.Now.AddDays(-1),
            ControlLogs = otherOrgLogs
        };
        _dbContext.SessionResults.Add(otherSession);
        await _dbContext.SaveChangesAsync();

        // Current logs identical to other org's session
        var currentLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Timestamp = DateTime.Now.AddDays(-1), Status = "Warning", Corner = "T1" }
        };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, currentLogs.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsFalse(result.IsStaleWarning); // Should not match other org's sessions
    }

    [TestMethod]
    public async Task DetermineControlLogStale_SessionsWithoutControlLogs_AreIgnored()
    {
        // Arrange
        var orgId = 1;
        var eventId = 10;

        var organization = new Organization { Id = orgId, ClientId = "test-client-id", ControlLogType = "TestType", ControlLogParams = "test-params" };
        var evt = new ConfigEvent { Id = eventId, OrganizationId = orgId, Name = "Test Event" };
        _dbContext.Organizations.Add(organization);
        _dbContext.Events.Add(evt);

        // Create session with empty control logs
        var session = new SessionResult
        {
            EventId = eventId,
            SessionId = 1,
            Start = DateTime.Now.AddDays(-1),
            ControlLogs = new List<ControlLogEntry>()
        };
        _dbContext.SessionResults.Add(session);
        await _dbContext.SaveChangesAsync();

        var currentLogs = new List<ControlLogEntry>
        {
            new ControlLogEntry { OrderId = 1, Car1 = "1", Timestamp = DateTime.Now }
        };

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, currentLogs.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        // Act
        var result = await _controller.GetControlLogStatistics(organization);

        // Assert
        Assert.IsFalse(result.IsStaleWarning); // Should ignore empty sessions
    }

    #endregion

    // Test controller to expose protected members
    private class TestOrganizationController : OrganizationControllerBase
    {
        public TestOrganizationController(
            ILoggerFactory loggerFactory,
            IDbContextFactory<TsContext> tsContext,
            IControlLogFactory controlLogFactory,
            AssetsCdn assetsCdn)
            : base(loggerFactory, tsContext, controlLogFactory, assetsCdn)
        {
        }
    }
}
