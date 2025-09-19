using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;

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


    public UpdateConsolidator(SessionContext sessionContext, ILoggerFactory loggerFactory)
    {
        this.sessionContext = sessionContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task<PatchUpdates> Process(PatchUpdates? update)
    {
        if (update == null)
            return new PatchUpdates([], []);

        // Apply the new update immediately to accumulated patches
        ApplyUpdateToAccumulatedPatches(update);

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

    private void ApplyUpdateToAccumulatedPatches(PatchUpdates update)
    {
        // Apply session changes
        foreach (var sessionChange in update.SessionPatches)
        {
            //var patch = sessionChange.GetChanges(sessionContext.SessionState);
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
            //var carState = sessionContext.GetCarByNumber(carChange.Number);
            if (carChange != null && carChange.Number != null)
            {
            //    var patch = carChange.GetChanges(carState);
            //    if (patch != null)
            //    {
                    if (!accumulatedCarPatches.TryGetValue(carChange.Number, out CarPositionPatch? value))
                    {
                        value = new CarPositionPatch { Number = carChange.Number };
                        accumulatedCarPatches[carChange.Number] = value;
                    }

                    accumulatedCarPatches[carChange.Number] = TimingCommon.Models.Mappers.CarPositionMapper.Merge(value, carChange);
            //    }
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
