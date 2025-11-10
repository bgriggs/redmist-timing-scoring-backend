using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.SessionMonitoring;

namespace RedMist.EventProcessor.Tests.EventStatus;

internal class DebugSessionMonitor : SessionMonitor
{
    public DebugSessionMonitor(int eventId, IDbContextFactory<TsContext> tsContext, SessionContext? sessionContext = null) 
        : base(eventId, tsContext, new DebugLoggerFactory(), sessionContext ?? CreateSessionContext(eventId))
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
            
        return new SessionContext(configuration);
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
    private readonly SessionMonitor sm;

    public DebugSessionMonitorV2(int eventId, IDbContextFactory<TsContext> tsContext, SessionContext? sessionContext = null) 
        : base(CreateConfiguration(eventId), tsContext, new DebugLoggerFactory(), sessionContext ?? CreateSessionContext(eventId))
    {
        // Get reference to the internal SessionMonitor to access its FinalizedSession event
        var field = typeof(SessionMonitorV2).GetField("sm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        sm = (SessionMonitor)field!.GetValue(this)!;
    }

    public event Action? FinalizedSession
    {
        add => sm.FinalizedSession += value;
        remove => sm.FinalizedSession -= value;
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
            
        return new SessionContext(configuration);
    }
}

// Create a debug-specific SessionMonitor that overrides database operations
internal class DebugSessionMonitorInternal : SessionMonitor
{
    public DebugSessionMonitorInternal(int eventId, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext) 
        : base(eventId, tsContext, loggerFactory, sessionContext)
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
}
