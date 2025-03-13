using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;

namespace RedMist.TimingAndScoringService.Tests.RMonitor;

internal class DebugSessionMonitor : SessionMonitor
{
    public DebugSessionMonitor(int eventId, IDbContextFactory<TsContext> tsContext) : base(eventId, tsContext, new DebugLoggerFactory())
    {
    }

    protected override Task SaveLastUpdatedTimestampUpdate(int eventId, int sessionId, CancellationToken stoppingToken = default)
    {
        return Task.CompletedTask;
    }

    protected override void FinalizeSession(object? state)
    {
        
    }

    protected override Task SetSessionAsLive(int eventId, int sessionId)
    {
        return Task.CompletedTask;
    }
}
