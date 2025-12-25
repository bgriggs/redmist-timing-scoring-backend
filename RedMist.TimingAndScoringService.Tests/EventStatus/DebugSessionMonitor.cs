using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.SessionMonitoring;
using RedMist.EventProcessor.Tests.Utilities;
using StackExchange.Redis;

namespace RedMist.EventProcessor.Tests.EventStatus;

internal class DebugSessionMonitor : SessionMonitor
{
    public DebugSessionMonitor(int eventId, IDbContextFactory<TsContext> tsContext, SessionContext? sessionContext = null, IConnectionMultiplexer? cacheMux = null) 
        : base(eventId, tsContext, new DebugLoggerFactory(), sessionContext ?? CreateSessionContext(eventId), cacheMux ?? CreateMockCacheMux())
    {
    }

    private static SessionContext CreateSessionContext(int eventId)
    {
        var configDict = new Dictionary<string, string?>
        {
            { "event_id", eventId.ToString() }
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        var dbContextFactory = CreateDbContextFactory();

        return new SessionContext(configuration, dbContextFactory);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var databaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(databaseName);
        var options = optionsBuilder.Options;
        return new TestDbContextFactory(options);
    }

    private static IConnectionMultiplexer CreateMockCacheMux()
    {
        var mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        var mockDatabase = new Mock<IDatabase>();
        mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(mockDatabase.Object);
        return mockConnectionMultiplexer.Object;
    }

    protected override Task SaveLastUpdatedTimestampAsync(int eventId, int sessionId, CancellationToken stoppingToken = default)
    {
        return Task.CompletedTask;
    }

    protected override void FinalizeSession()
    {
        ClearSession();
        FireFinalizedSession();
    }

    protected override async Task SetSessionAsLiveAsync(int eventId, int sessionId)
    {
        // No-op for test implementation - don't try to execute SQL
        await Task.CompletedTask;
    }
}

internal class DebugSessionMonitorV2 : SessionMonitorV2
{
    public DebugSessionMonitorV2(int eventId, IDbContextFactory<TsContext> tsContext, SessionContext? sessionContext = null, IConnectionMultiplexer? cacheMux = null) 
        : base(CreateConfiguration(eventId), tsContext, new DebugLoggerFactory(), sessionContext ?? CreateSessionContext(eventId), cacheMux ?? CreateMockCacheMux())
    {
    }

    public event Action? FinalizedSession
    {
        add => InnerSessionMonitor.FinalizedSession += value;
        remove => InnerSessionMonitor.FinalizedSession -= value;
    }

    private static IConfiguration CreateConfiguration(int eventId)
    {
        var configDict = new Dictionary<string, string?>
        {
            { "event_id", eventId.ToString() }
        };
        
        return new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
    }

        private static SessionContext CreateSessionContext(int eventId)
        {
            var configDict = new Dictionary<string, string?>
            {
                { "event_id", eventId.ToString() }
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configDict)
                .Build();

            var dbContextFactory = CreateDbContextFactory();

            return new SessionContext(configuration, dbContextFactory);
        }

            private static IDbContextFactory<TsContext> CreateDbContextFactory()
            {
                var databaseName = $"TestDatabase_{Guid.NewGuid()}";
                var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
                optionsBuilder.UseInMemoryDatabase(databaseName);
                var options = optionsBuilder.Options;
                return new TestDbContextFactory(options);
            }

            private static IConnectionMultiplexer CreateMockCacheMux()
            {
                var mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
                var mockDatabase = new Mock<IDatabase>();
                mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                    .Returns(mockDatabase.Object);
                return mockConnectionMultiplexer.Object;
            }
        }

// Create a debug-specific SessionMonitor that overrides database operations
internal class DebugSessionMonitorInternal : SessionMonitor
{
    public DebugSessionMonitorInternal(int eventId, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext, IConnectionMultiplexer? cacheMux = null) 
        : base(eventId, tsContext, loggerFactory, sessionContext, cacheMux ?? CreateMockCacheMux())
    {
    }

    protected override Task SaveLastUpdatedTimestampAsync(int eventId, int sessionId, CancellationToken stoppingToken = default)
    {
        return Task.CompletedTask;
    }

    protected override void FinalizeSession()
    {
        ClearSession();
        FireFinalizedSession();
    }

    protected override async Task SetSessionAsLiveAsync(int eventId, int sessionId)
    {
        // No-op for test implementation - don't try to execute SQL
        await Task.CompletedTask;
    }

    private static IConnectionMultiplexer CreateMockCacheMux()
    {
        var mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        var mockDatabase = new Mock<IDatabase>();
        mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(mockDatabase.Object);
        return mockConnectionMultiplexer.Object;
    }
}
