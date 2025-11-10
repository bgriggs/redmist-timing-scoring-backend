using BigMission.Shared.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.PipelineBlocks;

/// <summary>
/// Takes multiple session and car position updates over a short period of time and consolidates them into
/// a single update to reduce the number of updates sent to clients.
/// </summary>
public class UpdateConsolidator
{
    private ILogger Logger { get; }
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(20);
    private DateTime lastProcessTime = DateTime.MinValue;

    // Accumulated patches that get built incrementally
    private SessionStatePatch? accumulatedSessionPatch;
    private readonly Dictionary<string, CarPositionPatch> accumulatedCarPatches = [];
    private readonly SessionContext sessionContext;
    private readonly StatusAggregator statusAggregator;
    private readonly SemaphoreSlim processLock = new(1, 1);
    private readonly Debouncer debouncer = new(DebounceInterval);


    public UpdateConsolidator(SessionContext sessionContext, ILoggerFactory loggerFactory, StatusAggregator statusAggregator)
    {
        this.sessionContext = sessionContext;
        this.statusAggregator = statusAggregator;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task Process(PatchUpdates? update)
    {
        if (update == null || (update.SessionPatches.Count == 0 && update.CarPatches.Count == 0))
            return;
        await processLock.WaitAsync(sessionContext.CancellationToken);
        try
        {
            // Apply the new update immediately to accumulated patches
            ApplyUpdateToAccumulatedPatches(update);
        }
        finally
        {
            processLock.Release();
        }

        await debouncer.ExecuteAsync(async () =>
        {
            PatchUpdates patchesToSend;
            await processLock.WaitAsync(sessionContext.CancellationToken);
            try
            {
                patchesToSend = GetAndResetAccumulatedPatches();
                lastProcessTime = DateTime.UtcNow;
            }
            finally
            {
                processLock.Release();
            }
            if (patchesToSend.SessionPatches.Count > 0 || patchesToSend.CarPatches.Count > 0)
            {
                try
                {
                    await statusAggregator.Process(patchesToSend);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error sending consolidated updates to clients");
                }
            }
        });
    }

    private void ApplyUpdateToAccumulatedPatches(PatchUpdates update)
    {
        // Apply session changes
        foreach (var sessionChange in update.SessionPatches)
        {
            if (sessionChange != null)
            {
                if (accumulatedSessionPatch == null)
                {
                    accumulatedSessionPatch = sessionChange;
                }
                else
                {
                    accumulatedSessionPatch = TimingCommon.Models.Mappers.SessionStateMapper.Merge(accumulatedSessionPatch, sessionChange);
                }
            }
        }

        // Apply car changes
        foreach (var carChange in update.CarPatches)
        {
            if (carChange != null && carChange.Number != null)
            {
                if (!accumulatedCarPatches.TryGetValue(carChange.Number, out CarPositionPatch? value))
                {
                    value = new CarPositionPatch { Number = carChange.Number };
                    accumulatedCarPatches[carChange.Number] = value;
                }

                accumulatedCarPatches[carChange.Number] = TimingCommon.Models.Mappers.CarPositionMapper.Merge(value, carChange);
            }
        }
    }

    private PatchUpdates GetAndResetAccumulatedPatches()
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

        if (sessionPatch != null)
            return new PatchUpdates([sessionPatch], [.. carPatchList]);
        return new PatchUpdates([], [.. carPatchList]);
    }
}
