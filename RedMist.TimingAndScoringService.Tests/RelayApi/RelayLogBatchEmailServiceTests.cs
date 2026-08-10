using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.RelayApi.Services;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.RelayApi;

/// <summary>
/// Behavior of the relay log batching service: when a batch is considered complete, what the
/// report says, and what is cleaned up afterwards. The mail transport is replaced by an override
/// of the send step so no SMTP connection is ever attempted.
/// </summary>
[TestClass]
public class RelayLogBatchEmailServiceTests
{
    private const string ClientId = "relay-client-1";
    private static readonly string BatchKey = string.Format(Consts.RELAY_LOG_BATCH, ClientId);

    private Mock<IConnectionMultiplexer> mockMux = null!;
    private Mock<IDatabase> mockCache = null!;
    private IDbContextFactory<TsContext> dbFactory = null!;
    private FakeTimeProvider clock = null!;
    private Mock<ILogger<RelayLogBatchEmailService>> mockLogger = null!;
    private List<(LogLevel Level, string Message, Exception? Exception)> logged = null!;
    private EmailHelper emailHelper = null!;

    /// <summary>
    /// Substitutes the mail transport only. Everything above it — subject construction, report
    /// rendering, the success log and the swallowing of send failures — still runs for real, and no
    /// test can reach a live SMTP connection whatever order exceptions happen to be thrown in.
    /// </summary>
    private sealed class CapturingRelayLogBatchEmailService(
        ILogger<RelayLogBatchEmailService> logger,
        IConnectionMultiplexer cacheMux,
        IDbContextFactory<TsContext> tsContext,
        EmailHelper emailHelper,
        TimeProvider timeProvider)
        : RelayLogBatchEmailService(logger, cacheMux, tsContext, emailHelper, timeProvider)
    {
        public List<(string Subject, string Body, string To, string From)> Sent { get; } = [];
        public Action? OnSend { get; set; }

        internal override Task SendEmailAsync(string subject, string bodyHtml, string to, string from)
        {
            Sent.Add((subject, bodyHtml, to, from));
            OnSend?.Invoke();
            return Task.CompletedTask;
        }

        public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
    }

    [TestInitialize]
    public void Setup()
    {
        mockMux = new Mock<IConnectionMultiplexer>();
        mockCache = new Mock<IDatabase>();
        mockMux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockCache.Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"RelayLogBatchEmailServiceTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);

        clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        logged = [];
        mockLogger = new Mock<ILogger<RelayLogBatchEmailService>>();
        mockLogger.Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var exception = invocation.Arguments[3] as Exception;
                var formatter = invocation.Arguments[4];
                var message = formatter?.GetType().GetMethod("Invoke")?
                    .Invoke(formatter, [invocation.Arguments[2], exception])?.ToString() ?? string.Empty;
                lock (logged)
                {
                    logged.Add((level, message, exception));
                }
            }));

        emailHelper = new EmailHelper(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Email:Host", "smtp.invalid" },
                { "Email:Port", "587" },
                { "Email:Username", "user" },
                { "Email:Password", "password" },
            }).Build());
    }

    private CapturingRelayLogBatchEmailService CreateService(IDbContextFactory<TsContext>? factory = null)
        => new(mockLogger.Object, mockMux.Object, factory ?? dbFactory, emailHelper, clock);

    /// <summary>
    /// Runs the service loop, absorbing the cancellation that stops it. The failsafe token is a
    /// backstop that a healthy run never reaches: the only thing ending the loop is the
    /// <c>cts.Cancel()</c> fired from the mocked <c>SetMembersAsync</c>, and if that setup ever stops
    /// matching, the loop would otherwise spin on a delay whose token is never canceled and hang CI.
    /// </summary>
    private static async Task RunLoopAsync(CapturingRelayLogBatchEmailService service, CancellationToken token)
    {
        using var failsafe = new CancellationTokenSource(FailsafeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, failsafe.Token);
        try
        {
            await service.RunAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // The 30 second back-off observes the canceled token; that is the exit path after a failure.
        }

        Assert.IsFalse(failsafe.IsCancellationRequested,
            $"The service loop did not stop within {FailsafeTimeout.TotalSeconds:0}s.");
    }

    private static readonly TimeSpan FailsafeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Marks the batch key as present with a last-log time <paramref name="age"/> in the past.</summary>
    private void SetupBatch(TimeSpan age, params (string Name, string Value)[] fields)
    {
        var lastLogTicks = (clock.GetUtcNow().UtcDateTime - age).Ticks.ToString();
        mockCache.Setup(x => x.KeyExistsAsync(It.Is<RedisKey>(k => k == BatchKey), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        mockCache.Setup(x => x.HashGetAsync(It.Is<RedisKey>(k => k == BatchKey),
                It.Is<RedisValue>(f => f == "lastLogTimestamp"), It.IsAny<CommandFlags>()))
            .ReturnsAsync(lastLogTicks);
        mockCache.Setup(x => x.HashGetAllAsync(It.Is<RedisKey>(k => k == BatchKey), It.IsAny<CommandFlags>()))
            .ReturnsAsync([.. fields.Select(f => new HashEntry(f.Name, f.Value))]);
    }

    private async Task SeedOrganizationAsync(int orgId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            ClientId = $"client-{orgId}",
            Name = name,
            ShortName = "ORG",
        });
        await db.SaveChangesAsync();
    }

    #region Report body

    [TestMethod]
    public void BuildBatchEmailBody_IncludesClientOrganizationCountAndReportTime()
    {
        var body = RelayLogBatchEmailService.BuildBatchEmailBody(ClientId, "Sample Racing", 17,
            new Dictionary<string, string> { ["count"] = "17", ["clientId"] = ClientId },
            new DateTime(2026, 5, 1, 12, 34, 56, DateTimeKind.Utc));

        StringAssert.Contains(body, "<strong>Client ID:</strong> relay-client-1");
        StringAssert.Contains(body, "<strong>Organization:</strong> Sample Racing");
        StringAssert.Contains(body, "<strong>Total Logs:</strong> 17");
        StringAssert.Contains(body, "<strong>Report Time:</strong> 2026-05-01 12:34:56 UTC");
    }

    [TestMethod]
    public void BuildBatchEmailBody_OrdersLevelsByCountDescendingAndStripsThePrefix()
    {
        var body = RelayLogBatchEmailService.BuildBatchEmailBody(ClientId, "Sample Racing", 30,
            new Dictionary<string, string>
            {
                ["count"] = "30",
                ["clientId"] = ClientId,
                ["orgId"] = "5",
                ["level_Warning"] = "7",
                ["level_Error"] = "20",
                ["level_Information"] = "3",
            },
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        var error = body.IndexOf("<strong>Error:</strong> 20", StringComparison.Ordinal);
        var warning = body.IndexOf("<strong>Warning:</strong> 7", StringComparison.Ordinal);
        var info = body.IndexOf("<strong>Information:</strong> 3", StringComparison.Ordinal);

        Assert.IsGreaterThan(-1, error);
        Assert.IsGreaterThan(error, warning, "Levels are listed most frequent first.");
        Assert.IsGreaterThan(warning, info);
        Assert.DoesNotContain("level_", body, "The redis key prefix must not leak into the report.");
        Assert.DoesNotContain("<li><strong>count", body, "Bookkeeping fields are not log levels.");
    }

    [TestMethod]
    public void BuildBatchEmailBody_NoLevelCounters_OmitsTheLevelSection()
    {
        var body = RelayLogBatchEmailService.BuildBatchEmailBody(ClientId, "Sample Racing", 4,
            new Dictionary<string, string> { ["count"] = "4", ["clientId"] = ClientId, ["orgId"] = "5" },
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.DoesNotContain("Log Levels:", body);
        Assert.DoesNotContain("<ul>", body);
        StringAssert.Contains(body, "<strong>Total Logs:</strong> 4");
    }

    [TestMethod]
    public void BuildBatchEmailBody_NonNumericLevelCounter_Throws()
    {
        // The sort key is int.Parse'd, so a counter that is not a number takes the whole report down.
        Assert.ThrowsExactly<FormatException>(() => RelayLogBatchEmailService.BuildBatchEmailBody(
            ClientId, "Sample Racing", 2,
            new Dictionary<string, string>
            {
                ["count"] = "2",
                ["clientId"] = ClientId,
                ["level_Error"] = "many",
                ["level_Warning"] = "1",
            },
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    #endregion

    #region Batch readiness

    [TestMethod]
    public async Task ProcessClientBatchAsync_BatchKeyGone_UntracksTheClientAndSendsNothing()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(false);
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsEmpty(service.Sent);
        mockCache.Verify(x => x.SetRemoveAsync(It.Is<RedisKey>(k => k == Consts.RELAY_LOG_BATCH_TRACKING),
            It.Is<RedisValue>(v => v == ClientId), It.IsAny<CommandFlags>()), Times.Once,
            "An expired batch key must not leave the client tracked forever.");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_NoLastLogTimestamp_LeavesTheBatchAlone()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        mockCache.Setup(x => x.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsEmpty(service.Sent);
        mockCache.Verify(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
        mockCache.Verify(x => x.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_JustInsideTheInactivityWindow_DoesNotSend()
    {
        SetupBatch(TimeSpan.FromSeconds(59), ("count", "5"), ("clientId", ClientId), ("orgId", "0"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsEmpty(service.Sent, "The batch is still collecting; 59 seconds is under the one minute threshold.");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_ExactlyAtTheInactivityThreshold_Sends()
    {
        SetupBatch(TimeSpan.FromSeconds(60), ("count", "5"), ("clientId", ClientId), ("orgId", "0"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.HasCount(1, service.Sent, "The threshold check is exclusive, so exactly 60 seconds sends.");
        StringAssert.Contains(service.Sent[0].Body, "<strong>Total Logs:</strong> 5");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_CountOfZero_SendsNothingAndKeepsTheBatch()
    {
        SetupBatch(TimeSpan.FromMinutes(5), ("count", "0"), ("clientId", ClientId), ("orgId", "3"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsEmpty(service.Sent);
        mockCache.Verify(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never,
            "An empty batch is left in place rather than cleaned up.");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_MissingCountField_SendsNothing()
    {
        SetupBatch(TimeSpan.FromMinutes(5), ("clientId", ClientId), ("orgId", "3"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsEmpty(service.Sent);
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_MissingClientIdField_SendsNothing()
    {
        SetupBatch(TimeSpan.FromMinutes(5), ("count", "9"), ("orgId", "3"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsEmpty(service.Sent, "A half-written batch hash is skipped rather than reported.");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_MalformedLastLogTimestamp_IsContainedAndLogged()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        mockCache.Setup(x => x.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("not-a-tick-count");
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsEmpty(service.Sent);
        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
                && m.Message.Contains("Error processing batch for client") && m.Exception is FormatException),
            "The stored ticks are parsed with long.Parse, so a corrupt value fails the client's batch.");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_RedisFailure_IsContainedAndLogged()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
            && m.Message.Contains("Error processing batch for client") && m.Exception is RedisConnectionException));
    }

    #endregion

    #region Sending and cleanup

    [TestMethod]
    public async Task ProcessClientBatchAsync_ReadyBatch_SendsThenDeletesTheBatchAndUntracksTheClient()
    {
        await SeedOrganizationAsync(3, "Sample Racing");
        SetupBatch(TimeSpan.FromMinutes(2),
            ("count", "12"), ("clientId", ClientId), ("orgId", "3"), ("level_Error", "12"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.HasCount(1, service.Sent);
        var sent = service.Sent[0];
        Assert.AreEqual($"Red Mist Relay Log Report - {ClientId}", sent.Subject);
        Assert.AreEqual(RelayLogBatchEmailService.ReportRecipient, sent.To);
        Assert.AreEqual(RelayLogBatchEmailService.ReportSender, sent.From);
        StringAssert.Contains(sent.Body, $"<strong>Client ID:</strong> {ClientId}");
        StringAssert.Contains(sent.Body, "<strong>Organization:</strong> Sample Racing");
        StringAssert.Contains(sent.Body, "<strong>Total Logs:</strong> 12");
        StringAssert.Contains(sent.Body, "<li><strong>Error:</strong> 12</li>",
            "The raw batch hash is handed to the report builder.");
        Assert.IsTrue(logged.Any(m => m.Message.Contains("Successfully sent relay log batch email")),
            "A completed send is logged at information level.");

        mockCache.Verify(x => x.KeyDeleteAsync(It.Is<RedisKey>(k => k == BatchKey), It.IsAny<CommandFlags>()), Times.Once);
        mockCache.Verify(x => x.SetRemoveAsync(It.Is<RedisKey>(k => k == Consts.RELAY_LOG_BATCH_TRACKING),
            It.Is<RedisValue>(v => v == ClientId), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_TransportFails_SwallowsTheFailureAndStillClearsTheBatch()
    {
        SetupBatch(TimeSpan.FromMinutes(2), ("count", "4"), ("clientId", ClientId), ("orgId", "0"));
        var service = CreateService();
        service.OnSend = () => throw new InvalidOperationException("smtp refused");

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
            && m.Message.Contains("Failed to send relay log batch email") && m.Exception is InvalidOperationException));
        Assert.IsFalse(logged.Any(m => m.Message.Contains("Successfully sent")));
        mockCache.Verify(x => x.KeyDeleteAsync(It.Is<RedisKey>(k => k == BatchKey), It.IsAny<CommandFlags>()), Times.Once,
            "A failed send still clears the batch, so those relay logs are never reported.");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_OrgIdZero_ReportsUnknownOrganization()
    {
        SetupBatch(TimeSpan.FromMinutes(2), ("count", "1"), ("clientId", ClientId), ("orgId", "0"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        StringAssert.Contains(service.Sent.Single().Body, "<strong>Organization:</strong> Unknown Organization");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_OrgIdFieldAbsent_DefaultsToUnknownOrganization()
    {
        SetupBatch(TimeSpan.FromMinutes(2), ("count", "1"), ("clientId", ClientId));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        StringAssert.Contains(service.Sent.Single().Body, "<strong>Organization:</strong> Unknown Organization");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_OrganizationNotInDatabase_ReportsTheIdInstead()
    {
        SetupBatch(TimeSpan.FromMinutes(2), ("count", "1"), ("clientId", ClientId), ("orgId", "88"));
        var service = CreateService();

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        StringAssert.Contains(service.Sent.Single().Body, "<strong>Organization:</strong> Relay Logs Received from Organization 88");
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_OrganizationLookupFails_FallsBackToTheIdAndStillSends()
    {
        var failingFactory = new Mock<IDbContextFactory<TsContext>>();
        failingFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no database"));
        SetupBatch(TimeSpan.FromMinutes(2), ("count", "1"), ("clientId", ClientId), ("orgId", "88"));
        var service = CreateService(failingFactory.Object);

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        StringAssert.Contains(service.Sent.Single().Body, "<strong>Organization:</strong> Organization 88",
            "A database outage must not block the report.");
        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error && m.Message.Contains("Error getting organization name")));
    }

    [TestMethod]
    public async Task ProcessClientBatchAsync_CorruptLevelCounter_AbandonsTheEmailButStillClearsTheBatch()
    {
        // Exercises the real send path: the report cannot be built, the failure is swallowed, and the
        // batch is deleted anyway, so those relay logs are never reported. The body builder throws
        // before any mail transport is touched.
        SetupBatch(TimeSpan.FromMinutes(2),
            ("count", "3"), ("clientId", ClientId), ("orgId", "0"),
            ("level_Error", "not-a-number"), ("level_Warning", "1"));
        var service = new RelayLogBatchEmailService(mockLogger.Object, mockMux.Object, dbFactory, emailHelper, clock);

        await service.ProcessClientBatchAsync(ClientId, mockCache.Object, CancellationToken.None);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
            && m.Message.Contains("Failed to send relay log batch email") && m.Exception is FormatException));
        mockCache.Verify(x => x.KeyDeleteAsync(It.Is<RedisKey>(k => k == BatchKey), It.IsAny<CommandFlags>()), Times.Once);
    }

    #endregion

    #region Batch sweep and service loop

    [TestMethod]
    public async Task ProcessBatchesAsync_ProcessesEveryTrackedClient()
    {
        mockCache.Setup(x => x.SetMembersAsync(It.Is<RedisKey>(k => k == Consts.RELAY_LOG_BATCH_TRACKING),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync([new RedisValue("a"), new RedisValue("b"), new RedisValue("c")]);
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(false);
        var service = CreateService();

        await service.ProcessBatchesAsync(CancellationToken.None);

        foreach (var client in new[] { "a", "b", "c" })
        {
            mockCache.Verify(x => x.SetRemoveAsync(It.Is<RedisKey>(k => k == Consts.RELAY_LOG_BATCH_TRACKING),
                It.Is<RedisValue>(v => v == client), It.IsAny<CommandFlags>()), Times.Once);
        }
    }

    [TestMethod]
    public async Task ProcessBatchesAsync_CancellationDuringTheSweep_StopsBeforeTheNextClient()
    {
        using var cts = new CancellationTokenSource();
        mockCache.Setup(x => x.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([new RedisValue(ClientId), new RedisValue("second"), new RedisValue("third")]);
        SetupBatch(TimeSpan.FromMinutes(2), ("count", "1"), ("clientId", ClientId), ("orgId", "0"));
        var service = CreateService();
        service.OnSend = cts.Cancel;

        await service.ProcessBatchesAsync(cts.Token);

        Assert.HasCount(1, service.Sent, "The sweep must observe cancellation between clients.");
        mockCache.Verify(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_Canceled_ExitsTheLoopWithoutFaulting()
    {
        using var cts = new CancellationTokenSource();
        mockCache.Setup(x => x.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() =>
            {
                cts.Cancel();
                return [];
            });
        var service = CreateService();

        await RunLoopAsync(service, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Message.Contains("RelayLogBatchEmailService stopped")),
            "Cancellation is a clean shutdown, not a fault.");
    }

    [TestMethod]
    public async Task ExecuteAsync_SweepFails_LogsTheErrorAndBacksOff()
    {
        using var cts = new CancellationTokenSource();
        mockCache.Setup(x => x.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(() =>
            {
                cts.Cancel();
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down");
            });
        var service = CreateService();

        await RunLoopAsync(service, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
            && m.Message.Contains("Error in RelayLogBatchEmailService") && m.Exception is RedisConnectionException));
    }

    #endregion
}
