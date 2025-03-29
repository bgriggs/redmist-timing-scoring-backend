using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;

namespace RedMist.TimingAndScoringService.Tests.EventStatus;

internal class DebugSessionMonitor : SessionMonitor
{
    public DebugSessionMonitor(int eventId, IDbContextFactory<TsContext> tsContext) : base(eventId, tsContext, new DebugLoggerFactory())
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

    protected override Task SetSessionAsLiveAsync(int eventId, int sessionId)
    {
        return Task.CompletedTask;
    }
}
