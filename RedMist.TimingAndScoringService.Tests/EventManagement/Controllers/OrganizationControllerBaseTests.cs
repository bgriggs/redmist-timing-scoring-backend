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
using RedMist.TimingCommon.Models.Configuration;
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
    private Mock<IHttpClientFactory> _mockHttpClientFactory = null!;
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
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();

        var mockConfiguration = new Mock<IConfiguration>();
        mockConfiguration.Setup(c => c["Assets:StorageZoneName"]).Returns("test-zone");
        mockConfiguration.Setup(c => c["Assets:StorageAccessKey"]).Returns("test-key");
        mockConfiguration.Setup(c => c["Assets:MainReplicationRegion"]).Returns("test-region");
        mockConfiguration.Setup(c => c["Assets:ApiAccessKey"]).Returns("test-api-key");
        mockConfiguration.Setup(c => c["Assets:CdnId"]).Returns("test-cdn-id");

        _mockAssetsCdn = new Mock<AssetsCdn>(mockConfiguration.Object, _mockLoggerFactory.Object, _mockHttpClientFactory.Object);

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

    private void SetupDefaultUser() => SetUser("test-client-id");

    private void SetUser(string clientId)
    {
        var claims = new List<Claim>
        {
            new Claim("client_id", clientId)
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };
    }

    /// <summary>
    /// Seeds the caller's organization (id 1) and an unrelated organization (id 2) so ownership
    /// filtering can be asserted.
    /// </summary>
    private async Task SeedOrganizationsAsync()
    {
        _dbContext.Organizations.Add(new Organization { Id = 1, ClientId = "test-client-id", Name = "Mine", ShortName = "M" });
        _dbContext.Organizations.Add(new Organization { Id = 2, ClientId = "other-client-id", Name = "Theirs", ShortName = "T" });
        await _dbContext.SaveChangesAsync();
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

    /// <summary>
    /// Every compared field must take part in the stale-log comparison. If any one of them stopped
    /// being compared, an organization that re-uploaded a genuinely new control log would be warned
    /// that it is stale.
    /// </summary>
    [TestMethod]
    [DataRow("OrderId")]
    [DataRow("Car1")]
    [DataRow("Car2")]
    [DataRow("Timestamp")]
    [DataRow("Status")]
    [DataRow("Corner")]
    [DataRow("Note")]
    [DataRow("OtherNotes")]
    public async Task DetermineControlLogStale_SingleFieldDiffers_IsNotConsideredStale(string changedField)
    {
        var organization = new Organization { Id = 1, ClientId = "test-client-id", ControlLogType = "TestType", ControlLogParams = "p" };
        _dbContext.Organizations.Add(organization);
        _dbContext.Events.Add(new ConfigEvent { Id = 10, OrganizationId = 1, Name = "Test Event" });

        var timestamp = new DateTime(2026, 3, 1, 10, 0, 0);
        ControlLogEntry MakeEntry() => new()
        {
            OrderId = 1,
            Car1 = "1",
            Car2 = "2",
            Timestamp = timestamp,
            Status = "Warning",
            Corner = "T1",
            Note = "Note",
            OtherNotes = "Other",
        };

        _dbContext.SessionResults.Add(new SessionResult
        {
            EventId = 10,
            SessionId = 1,
            Start = new DateTime(2026, 2, 1),
            ControlLogs = [MakeEntry()],
        });
        await _dbContext.SaveChangesAsync();

        var current = MakeEntry();
        switch (changedField)
        {
            case "OrderId": current.OrderId = 99; break;
            case "Car1": current.Car1 = "99"; break;
            case "Car2": current.Car2 = "99"; break;
            case "Timestamp": current.Timestamp = timestamp.AddSeconds(1); break;
            case "Status": current.Status = "Different"; break;
            case "Corner": current.Corner = "T9"; break;
            case "Note": current.Note = "Different"; break;
            case "OtherNotes": current.OtherNotes = "Different"; break;
            default: Assert.Fail($"Unhandled field {changedField}"); break;
        }

        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, new[] { current }.AsEnumerable()));
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);

        var result = await _controller.GetControlLogStatistics(organization);

        Assert.IsFalse(result.IsStaleWarning, $"A difference in {changedField} should not be treated as the same log.");
    }

    #endregion

    #region GetControlLogStatistics branch coverage

    [TestMethod]
    [DataRow("Default")]
    [DataRow("None")]
    public async Task GetControlLogStatistics_SentinelControlLogType_NeverConnects(string controlLogType)
    {
        var organization = new Organization { Id = 1, ControlLogType = controlLogType, ControlLogParams = "p" };

        var result = await _controller.GetControlLogStatistics(organization);

        Assert.IsFalse(result.IsConnected);
        Assert.AreEqual(0, result.TotalEntries);
        _mockControlLogFactory.Verify(x => x.CreateControlLog(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task GetControlLogStatistics_ControlLogThrows_ReturnsEmptyStatisticsInsteadOfFailing()
    {
        var organization = new Organization { Id = 1, ControlLogType = "TestType", ControlLogParams = "p" };
        _mockControlLogFactory.Setup(x => x.CreateControlLog("TestType")).Returns(_mockControlLog.Object);
        _mockControlLog.Setup(x => x.LoadControlLogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("control log unavailable"));

        var result = await _controller.GetControlLogStatistics(organization);

        Assert.IsFalse(result.IsConnected);
        Assert.AreEqual(0, result.TotalEntries);
        Assert.IsFalse(result.IsStaleWarning);
    }

    #endregion

    #region LoadOrganization

    [TestMethod]
    public async Task LoadOrganization_NoOrganizationForClient_ReturnsNotFound()
    {
        await SeedOrganizationsAsync();
        SetUser("unknown-client-id");

        var result = await _controller.LoadOrganization();

        Assert.IsInstanceOfType<NotFoundResult>(result.Result);
    }

    [TestMethod]
    public async Task LoadOrganization_ReturnsOrganizationMatchingClientIdOnly()
    {
        await SeedOrganizationsAsync();

        var result = await _controller.LoadOrganization();

        var org = (result.Result as OkObjectResult)?.Value as Organization;
        Assert.IsNotNull(org);
        Assert.AreEqual(1, org.Id);
        Assert.AreEqual("Mine", org.Name);
    }

    [TestMethod]
    public async Task LoadOrganization_NoLogo_SubstitutesDefaultImage()
    {
        await SeedOrganizationsAsync();
        _dbContext.DefaultOrgImages.Add(new DefaultOrgImage { Id = 1, ImageData = [1, 2, 3] });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.LoadOrganization();

        var org = (result.Result as OkObjectResult)?.Value as Organization;
        Assert.IsNotNull(org);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, org.Logo);
        // The substitution is for the response only and must not be written back to the organization row.
        Assert.IsNull(_dbContext.Organizations.AsNoTracking().Single(o => o.Id == 1).Logo);
    }

    [TestMethod]
    public async Task LoadOrganization_WithLogo_KeepsOrganizationLogo()
    {
        _dbContext.Organizations.Add(new Organization { Id = 1, ClientId = "test-client-id", Name = "Mine", ShortName = "M", Logo = [9, 9] });
        _dbContext.DefaultOrgImages.Add(new DefaultOrgImage { Id = 1, ImageData = [1, 2, 3] });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.LoadOrganization();

        var org = (result.Result as OkObjectResult)?.Value as Organization;
        Assert.IsNotNull(org);
        CollectionAssert.AreEqual(new byte[] { 9, 9 }, org.Logo);
    }

    #endregion

    #region UpdateOrganization

    [TestMethod]
    public async Task UpdateOrganization_NoOrganizationForClient_ReturnsNotFound()
    {
        await SeedOrganizationsAsync();
        SetUser("unknown-client-id");

        var result = await _controller.UpdateOrganization(new Organization { Id = 1, Website = "https://hijack.test" });

        Assert.IsInstanceOfType<NotFoundResult>(result);
        // Website is one of the fields UpdateOrganization actually copies, so this proves nothing was written.
        Assert.IsNull(_dbContext.Organizations.AsNoTracking().Single(o => o.Id == 1).Website);
    }

    [TestMethod]
    public async Task LoadOrganization_TokenWithoutClientIdClaim_ReturnsNotFound()
    {
        await SeedOrganizationsAsync();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuthType")) }
        };

        var result = await _controller.LoadOrganization();

        Assert.IsInstanceOfType<NotFoundResult>(result.Result);
    }

    [TestMethod]
    public async Task UpdateOrganization_UpdatesCallersOrganizationOnly_IgnoringSuppliedId()
    {
        await SeedOrganizationsAsync();

        // Id 2 belongs to another organization; the caller's client_id is what selects the row.
        var result = await _controller.UpdateOrganization(new Organization
        {
            Id = 2,
            Website = "https://mine.test",
            ControlLogType = "Sheets",
            ControlLogParams = "params",
            RMonitorIp = "10.0.0.1",
            RMonitorPort = 1234,
            MultiloopIp = "10.0.0.2",
            MultiloopPort = 5678,
            OrbitsLogsPath = @"C:\logs",
            FlagtronicsUrl = "https://flags.test",
            FlagtronicsApiKey = "key",
            ShowControlLogConnection = true,
            ShowX2Connection = true,
            ShowMultiloopConnection = true,
            ShowOrbitsLogsConnection = true,
            ShowFlagtronicsConnection = true,
            Classes = [new ClassMetadata { Name = "GT", Order = 1, ColorHex = "#00FF00" }],
            X2 = new X2Configuration { Server = "x2.test", Username = "u", Password = "p" },
        });

        Assert.IsInstanceOfType<OkResult>(result);
        var mine = _dbContext.Organizations.AsNoTracking().Single(o => o.Id == 1);
        Assert.AreEqual("https://mine.test", mine.Website);
        Assert.AreEqual("Sheets", mine.ControlLogType);
        Assert.AreEqual("params", mine.ControlLogParams);
        Assert.AreEqual("10.0.0.1", mine.RMonitorIp);
        Assert.AreEqual(1234, mine.RMonitorPort);
        Assert.AreEqual("10.0.0.2", mine.MultiloopIp);
        Assert.AreEqual(5678, mine.MultiloopPort);
        Assert.AreEqual(@"C:\logs", mine.OrbitsLogsPath);
        Assert.AreEqual("https://flags.test", mine.FlagtronicsUrl);
        Assert.AreEqual("key", mine.FlagtronicsApiKey);
        Assert.IsTrue(mine.ShowControlLogConnection);
        Assert.IsTrue(mine.ShowX2Connection);
        Assert.IsTrue(mine.ShowMultiloopConnection);
        Assert.IsTrue(mine.ShowOrbitsLogsConnection);
        Assert.IsTrue(mine.ShowFlagtronicsConnection);
        Assert.AreEqual("GT", mine.Classes.Single().Name);
        Assert.AreEqual("x2.test", mine.X2.Server);

        var theirs = _dbContext.Organizations.AsNoTracking().Single(o => o.Id == 2);
        Assert.IsNull(theirs.Website);
        Assert.AreEqual(string.Empty, theirs.ControlLogType);
    }

    [TestMethod]
    public async Task UpdateOrganization_DoesNotOverwriteIdentityFields()
    {
        await SeedOrganizationsAsync();

        await _controller.UpdateOrganization(new Organization
        {
            Id = 1,
            ClientId = "stolen-client-id",
            Name = "Renamed",
            ShortName = "R",
            Website = "https://mine.test",
        });

        var mine = _dbContext.Organizations.AsNoTracking().Single(o => o.Id == 1);
        Assert.AreEqual("test-client-id", mine.ClientId);
        Assert.AreEqual("Mine", mine.Name);
        Assert.AreEqual("M", mine.ShortName);
        Assert.AreEqual("https://mine.test", mine.Website);
    }

    [TestMethod]
    public async Task UpdateOrganization_NullLogo_LeavesStoredLogoUntouched()
    {
        _dbContext.Organizations.Add(new Organization { Id = 1, ClientId = "test-client-id", Name = "Mine", ShortName = "M", Logo = [7, 7] });
        await _dbContext.SaveChangesAsync();

        await _controller.UpdateOrganization(new Organization { Id = 1, Logo = null });

        CollectionAssert.AreEqual(new byte[] { 7, 7 }, _dbContext.Organizations.AsNoTracking().Single().Logo);
    }

    [TestMethod]
    public async Task UpdateOrganization_EmptyLogo_ClearsStoredLogo()
    {
        _dbContext.Organizations.Add(new Organization { Id = 1, ClientId = "test-client-id", Name = "Mine", ShortName = "M", Logo = [7, 7] });
        await _dbContext.SaveChangesAsync();

        // No DefaultOrgImage row, so the clear happens without a CDN round trip.
        var result = await _controller.UpdateOrganization(new Organization { Id = 1, Logo = [] });

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.IsNull(_dbContext.Organizations.AsNoTracking().Single().Logo);
    }

    #endregion

    #region Organization administrators

    [TestMethod]
    public async Task LoadOrganizationAdministratorsAsync_NoOrganizationForClient_ReturnsNotFound()
    {
        await SeedOrganizationsAsync();
        SetUser("unknown-client-id");

        var result = await _controller.LoadOrganizationAdministratorsAsync();

        Assert.IsInstanceOfType<NotFoundResult>(result.Result);
    }

    [TestMethod]
    public async Task LoadOrganizationAdministratorsAsync_ReturnsAdminsOfCallersOrganizationOnly()
    {
        await SeedOrganizationsAsync();
        _dbContext.UserOrganizationMappings.AddRange(
            new UserOrganizationMapping { OrganizationId = 1, Username = "admin@mine.test", Role = "admin" },
            new UserOrganizationMapping { OrganizationId = 1, Username = "viewer@mine.test", Role = "viewer" },
            new UserOrganizationMapping { OrganizationId = 2, Username = "admin@theirs.test", Role = "admin" });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.LoadOrganizationAdministratorsAsync();

        var emails = (result.Result as OkObjectResult)?.Value as List<string>;
        Assert.IsNotNull(emails);
        CollectionAssert.AreEqual(new[] { "admin@mine.test" }, emails);
    }

    [TestMethod]
    public async Task SaveOrganizationAdministratorsAsync_NoOrganizationForClient_ReturnsNotFound()
    {
        await SeedOrganizationsAsync();
        SetUser("unknown-client-id");

        var result = await _controller.SaveOrganizationAdministratorsAsync(["a@b.test"]);

        Assert.IsInstanceOfType<NotFoundResult>(result);
        Assert.AreEqual(0, _dbContext.UserOrganizationMappings.Count());
    }

    [TestMethod]
    public async Task SaveOrganizationAdministratorsAsync_ReplacesMappingsForCallersOrganizationOnly()
    {
        await SeedOrganizationsAsync();
        _dbContext.UserOrganizationMappings.AddRange(
            new UserOrganizationMapping { OrganizationId = 1, Username = "old@mine.test", Role = "admin" },
            new UserOrganizationMapping { OrganizationId = 1, Username = "viewer@mine.test", Role = "viewer" },
            new UserOrganizationMapping { OrganizationId = 2, Username = "admin@theirs.test", Role = "admin" });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.SaveOrganizationAdministratorsAsync(["new@mine.test"]);

        Assert.IsInstanceOfType<OkResult>(result);
        var mine = _dbContext.UserOrganizationMappings.AsNoTracking().Where(m => m.OrganizationId == 1).ToList();
        Assert.AreEqual(1, mine.Count);
        Assert.AreEqual("new@mine.test", mine[0].Username);
        Assert.AreEqual("admin", mine[0].Role);
        Assert.AreEqual(1, _dbContext.UserOrganizationMappings.AsNoTracking().Count(m => m.OrganizationId == 2));
    }

    [TestMethod]
    public async Task SaveOrganizationAdministratorsAsync_EmptyList_RemovesAllMappingsForOrganization()
    {
        await SeedOrganizationsAsync();
        _dbContext.UserOrganizationMappings.Add(new UserOrganizationMapping { OrganizationId = 1, Username = "old@mine.test", Role = "admin" });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.SaveOrganizationAdministratorsAsync([]);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual(0, _dbContext.UserOrganizationMappings.AsNoTracking().Count(m => m.OrganizationId == 1));
    }

    [TestMethod]
    public async Task SaveOrganizationAdministratorsAsync_ResavingAnUnchangedAdministrator_KeepsTheMapping()
    {
        await SeedOrganizationsAsync();
        _dbContext.UserOrganizationMappings.Add(new UserOrganizationMapping { OrganizationId = 1, Username = "admin@mine.test", Role = "admin" });
        await _dbContext.SaveChangesAsync();

        var result = await _controller.SaveOrganizationAdministratorsAsync(["admin@mine.test"]);

        Assert.IsInstanceOfType<OkResult>(result);
        var mine = _dbContext.UserOrganizationMappings.AsNoTracking().Where(m => m.OrganizationId == 1).ToList();
        Assert.AreEqual(1, mine.Count);
        Assert.AreEqual("admin@mine.test", mine[0].Username);
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
