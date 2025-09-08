using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared.Hubs;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Responsible for taking session and car position patches and applying them and sending them to clients.
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


    public async Task Process((SessionStatePatch? sessionPatch, CarPositionPatch[] carPatches) updates)
    {
        var tasks = new List<Task>();
        using (await sessionContext.SessionStateLock.AcquireWriteLockAsync())
        {
            if (updates.sessionPatch != null)
            {
                TimingCommon.Models.Mappers.SessionStateMapper.ApplyPatch(updates.sessionPatch, sessionContext.SessionState);
                var st = hubContext.Clients.Group(sessionContext.EventId.ToString()).SendAsync("ReceiveSessionPatch", updates.sessionPatch, sessionContext.CancellationToken);
                tasks.Add(st);
            }


            var ct = hubContext.Clients.Group(sessionContext.EventId.ToString()).SendAsync("ReceiveCarPatches", updates.carPatches, sessionContext.CancellationToken);
            tasks.Add(ct);
            foreach (var update in updates.carPatches)
            {
                if (update.Number == null)
                    continue;

                var c = sessionContext.GetCarByNumber(update.Number);
                if (c != null)
                {
                    TimingCommon.Models.Mappers.CarPositionMapper.ApplyPatch(update, c);
                }
            }
        }
        await Task.WhenAll(tasks);
    }
}

/// <summary>
/// Takes multiple session and car position updates over a short period of time and consolidates them into
/// a single update to reduce the number of updates sent to clients.
/// </summary>
public class UpdateConsolidator(SessionContext sessionContext)
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(20);
    private readonly SemaphoreSlim processingLock = new(1, 1);
    private DateTime lastProcessTime = DateTime.MinValue;
    
    // Accumulated patches that get built incrementally
    private SessionStatePatch? accumulatedSessionPatch;
    private readonly Dictionary<string, CarPositionPatch> accumulatedCarPatches = [];
    
    public async Task<(SessionStatePatch?, CarPositionPatch[])> Process(SessionStateUpdate? update)
    {
        await processingLock.WaitAsync();
        try
        {
            // Apply the new update immediately to accumulated patches
            if (update != null)
            {
                ApplyUpdateToAccumulatedPatches(update);
            }

            var now = DateTime.UtcNow;
            var timeSinceLastProcess = now - lastProcessTime;

            // If it's been less than 20ms since the last process, wait for the remainder
            if (timeSinceLastProcess < DebounceInterval)
            {
                var remainingWait = DebounceInterval - timeSinceLastProcess;
                await Task.Delay(remainingWait, sessionContext.CancellationToken);
            }

            // Return the accumulated patches and reset for next cycle
            var result = GetAndResetAccumulatedPatches();
            lastProcessTime = DateTime.UtcNow;

            return result;
        }
        finally
        {
            processingLock.Release();
        }
    }

    private void ApplyUpdateToAccumulatedPatches(SessionStateUpdate update)
    {
        // Apply session changes
        foreach (var sessionChange in update.SessionChanges)
        {
            var patch = sessionChange.GetChanges(sessionContext.SessionState);
            if (patch != null)
            {
                if (accumulatedSessionPatch == null)
                {
                    accumulatedSessionPatch = patch;
                }
                else
                {
                    accumulatedSessionPatch = TimingCommon.Models.Mappers.SessionStateMapper.Merge(accumulatedSessionPatch, patch);
                }
            }
        }

        // Apply car changes
        foreach (var carChange in update.CarChanges)
        {
            var carState = sessionContext.GetCarByNumber(carChange.Number);
            if (carState != null)
            {
                var patch = carChange.GetChanges(carState);
                if (patch != null)
                {
                    if (!accumulatedCarPatches.ContainsKey(carChange.Number))
                    {
                        accumulatedCarPatches[carChange.Number] = new CarPositionPatch { Number = carChange.Number };
                    }
                    
                    accumulatedCarPatches[carChange.Number] = TimingCommon.Models.Mappers.CarPositionMapper.Merge(
                        accumulatedCarPatches[carChange.Number], patch);
                }
            }
        }
    }

    private (SessionStatePatch?, CarPositionPatch[]) GetAndResetAccumulatedPatches()
    {
        var sessionPatch = accumulatedSessionPatch;
        
        // Filter car patches to only include those with meaningful changes
        var carPatchList = new List<CarPositionPatch>();
        foreach (var kvp in accumulatedCarPatches)
        {
            var properties = TimingCommon.Models.Mappers.CarPositionMapper.GetChangedProperties(kvp.Value);
            if (properties.Length > 1) // More than just the Number property
            {
                carPatchList.Add(kvp.Value);
            }
        }

        // Reset accumulated patches for next cycle
        accumulatedSessionPatch = null;
        accumulatedCarPatches.Clear();

        return (sessionPatch, carPatchList.ToArray());
    }
}