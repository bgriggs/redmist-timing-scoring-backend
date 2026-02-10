using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.PipelineBlocks;

/// <summary>
/// Responsible for taking session and car position patches and sending them to clients.
/// </summary>
public class StatusAggregator
{
    private readonly IHubContext<StatusHub> hubContext;
    private readonly SessionContext sessionContext;
    private readonly ILogger logger;
    public event Action<SessionStatePatch>? OnSessionPatch;
    public event Action<IEnumerable<CarPositionPatch>>? OnCarPatch;

    public StatusAggregator(IHubContext<StatusHub> hubContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        this.hubContext = hubContext;
        this.sessionContext = sessionContext;
        this.logger = loggerFactory.CreateLogger<StatusAggregator>();
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
                OnSessionPatch?.Invoke(sp);
            }

            // Legacy support
            //var payload = sessionContext.SessionState.ToPayload();
            //payload.EventEntries.Clear(); // Event entries are sent separately as patches
            if (updates.CarPatches.Count > 0)
            {
                var ct = hubContext.Clients.Group(subKey).SendAsync("ReceiveCarPatches", updates.CarPatches, sessionContext.CancellationToken);
                tasks.Add(ct);
                OnCarPatch?.Invoke(updates.CarPatches);

                //var updatedCarNumbers = updates.CarPatches.Select(p => p.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);
                //payload.CarPositionUpdates.AddRange(payload.CarPositions.Where(c => updatedCarNumbers.Contains(c.Number)));
            }
            //payload.CarPositions.Clear();

            //// Legacy support
            //var json = JsonSerializer.Serialize(payload);
            //var pt = hubContext.Clients.Group(eventId).SendAsync("ReceiveMessage", json, sessionContext.CancellationToken);
            //tasks.Add(pt);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED to send patches to group {subKey}", subKey);
            throw;
        }
    }
}