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


    public Task Process(PatchUpdates? update)
    {
        if (update == null)
            return Task.CompletedTask;
        return Process([update]);
    }

    /// <summary>
    /// Takes everything one pipeline pass produced and publishes it as a single update.
    ///
    /// A pass yields a patch set per stage - the timing source's own changes, then position, pit,
    /// driver, video, penalty and telemetry enrichment layered on top - and all of them describe
    /// the same instant. Debouncing them one at a time charged the interval once per stage and put
    /// the field out in as many pieces: the caller waits out the delay on every call, so a message
    /// with eight populated stages spent most of a second publishing work that had already been
    /// computed. Merging first means one interval and one send per pass however many stages it
    /// touched.
    /// </summary>
    public async Task Process(IReadOnlyList<PatchUpdates>? updates)
    {
        if (updates == null || updates.Count == 0)
            return;

        var accumulated = false;
        await processLock.WaitAsync(sessionContext.CancellationToken);
        try
        {
            foreach (var update in updates)
            {
                if (update == null || (update.SessionPatches.Count == 0 && update.CarPatches.Count == 0))
                    continue;

                // Apply the new update immediately to accumulated patches
                ApplyUpdateToAccumulatedPatches(update);
                accumulated = true;
            }
        }
        finally
        {
            processLock.Release();
        }

        if (!accumulated)
            return;

        await debouncer.ExecuteAsync(SendAccumulatedAsync);
    }

    /// <summary>
    /// Sends whatever has accumulated since the last send.
    ///
    /// One drain covers everything, because the debouncer only turns calls away during its delay,
    /// not while this is running - and a call it turns away has already merged its patches, since
    /// <see cref="Process(IReadOnlyList{PatchUpdates})"/> merges before it ever reaches the
    /// debouncer. So the cycle already pending when a call is dropped is guaranteed to drain after
    /// that call's patches were put in, and a call arriving while this is in flight starts a cycle
    /// of its own.
    /// </summary>
    private async Task SendAccumulatedAsync()
    {
        PatchUpdates patchesToSend;
        await processLock.WaitAsync(sessionContext.CancellationToken);
        try
        {
            patchesToSend = GetAndResetAccumulatedPatches();
        }
        finally
        {
            processLock.Release();
        }

        if (patchesToSend.SessionPatches.Count == 0 && patchesToSend.CarPatches.Count == 0)
            return;

        try
        {
            await statusAggregator.Process(patchesToSend);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error sending consolidated updates to clients");
        }
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
                kvp.Value.EventId = sessionContext.EventId.ToString();
                kvp.Value.SessionId = sessionContext.SessionState.SessionId.ToString();
                carPatchList.Add(kvp.Value);
            }
        }

        // Reset accumulated patches for next cycle
        accumulatedSessionPatch = null;
        accumulatedCarPatches.Clear();

        if (sessionPatch != null)
        {
            sessionPatch.EventId = sessionContext.EventId;
            sessionPatch.SessionId = sessionContext.SessionState.SessionId;
            return new PatchUpdates([sessionPatch], [.. carPatchList]);
        }
        return new PatchUpdates([], [.. carPatchList]);
    }
}
