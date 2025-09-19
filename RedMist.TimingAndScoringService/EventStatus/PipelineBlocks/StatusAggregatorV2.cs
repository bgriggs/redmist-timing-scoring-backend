using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared.Hubs;

namespace RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;

/// <summary>
/// Responsible for taking session and car position patches and sending them to clients.
/// </summary>
public class StatusAggregatorV2
{
    private readonly IHubContext<StatusHub> hubContext;
    private readonly SessionContext sessionContext;

    private ILogger Logger { get; }


    public StatusAggregatorV2(IHubContext<StatusHub> hubContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        this.hubContext = hubContext;
        this.sessionContext = sessionContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task Process(PatchUpdates updates)
    {
        var tasks = new List<Task>();
        var eventId = sessionContext.EventId.ToString();
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync())
        {
            foreach (var sp in updates.SessionPatches)
            {
                var st = hubContext.Clients.Group(eventId).SendAsync("ReceiveSessionPatch", sp, sessionContext.CancellationToken);
                tasks.Add(st);
            }

            if (updates.CarPatches.Count > 0)
            {
                var ct = hubContext.Clients.Group(eventId).SendAsync("ReceiveCarPatches", updates.CarPatches, sessionContext.CancellationToken);
                tasks.Add(ct);
            }
        }
        await Task.WhenAll(tasks);
    }
}