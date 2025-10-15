using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.TimingCommon.Extensions;
using System.Text.Json;

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
        var subKey = string.Format(Consts.EVENT_SUB_V2, eventId);
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync())
        {
            foreach (var sp in updates.SessionPatches)
            {
                var st = hubContext.Clients.Group(subKey).SendAsync("ReceiveSessionPatch", sp, sessionContext.CancellationToken);
                tasks.Add(st);
            }

            // Legacy support
            var payload = sessionContext.SessionState.ToPayload();
            payload.EventEntries.Clear(); // Event entries are sent separately as patches
            if (updates.CarPatches.Count > 0)
            {
                var ct = hubContext.Clients.Group(subKey).SendAsync("ReceiveCarPatches", updates.CarPatches, sessionContext.CancellationToken);
                tasks.Add(ct);

                var updatedCarNumbers = updates.CarPatches.Select(p => p.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);
                payload.CarPositionUpdates.AddRange(payload.CarPositions.Where(c => updatedCarNumbers.Contains(c.Number)));
            }
            payload.CarPositions.Clear();

            // Legacy support
            var json = JsonSerializer.Serialize(payload);
            var pt = hubContext.Clients.Group(eventId).SendAsync("ReceiveMessage", json, sessionContext.CancellationToken);
            tasks.Add(pt);
        }
        await Task.WhenAll(tasks);
    }
}