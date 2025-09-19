using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using Riok.Mapperly.Abstractions;
using System.Diagnostics;

namespace RedMist.TimingAndScoringService.EventStatus.PositionEnricher;

/// <summary>
/// This runs after the RMonitor processor and provides changes to Car's class position, gap, 
/// difference and positions gained or lost.
/// </summary>
public class PositionDataEnricher
{
    private ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SessionContext sessionContext;
    private readonly PositionMetadataProcessor positionMetadataProcessor = new();
    private readonly CarPositionMapper carPositionMapper = new();
    private readonly Dictionary<string, int> lastMLOverallStartingPositions = [];
    private readonly Dictionary<string, int> mlInClassStartingPositions = [];


    public PositionDataEnricher(IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        this.tsContext = tsContext ?? throw new ArgumentNullException(nameof(tsContext));
        Logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(GetType().Name);
        this.sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
    }


    /// <summary>
    /// Takes the current session state and finds the position related metadata for each car. These changes 
    /// are returned as a SessionStateUpdate. The session state is not modified.
    /// </summary>
    /// <returns></returns>
    public PatchUpdates? Process()
    {
        var carChanges = new List<CarPositionPatch>();
        try
        {
            // Make a deep copy of the car positions so that the position processor does not modify the session state.
            var originalCarPositions = sessionContext.SessionState.CarPositions.ToList();

            if (!originalCarPositions.Any(c => c.OverallPosition > 0))
            {
                Logger.LogWarning("Cannot update positions. All car positions are zero.");
                Trace.WriteLine("Cannot update positions. All car positions are zero.");
                return null;
            }

            var copiedCarPositions = carPositionMapper.CloneCarPositions(originalCarPositions);
            ApplyStartingPositions(copiedCarPositions);

            // Update the copies with position metadata
            positionMetadataProcessor.UpdateCarPositions(copiedCarPositions);

            // Find changes from the original session state CarPositions and the updated ones
            // and create CarPositionPatches to return in a SessionStateUpdate.
            for (int i = 0; i < originalCarPositions.Count && i < copiedCarPositions.Count; i++)
            {
                var original = originalCarPositions[i];
                var updated = copiedCarPositions[i];

                var patch = TimingCommon.Models.Mappers.CarPositionMapper.CreatePatch(original, updated);

                if (TimingCommon.Models.Mappers.CarPositionMapper.IsValidPatch(patch))
                {
                    patch.Number = original.Number; // Ensure number is set
                    var stateChange = new PositionMetadataStateUpdate(patch);
                    var p = stateChange.ApplyCarChange(sessionContext);
                    if (p != null)
                        carChanges.Add(p);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing position metadata.");
            return null;
        }
        return new PatchUpdates([], [.. carChanges]);
    }

    /// <summary>
    /// Starting position will either come from multiloop or be inferred from RMonitor data.
    /// Determine which is appropriate and apply to the copied car positions.
    /// </summary>
    /// <param name="copiedCarPositions"></param>
    private void ApplyStartingPositions(List<CarPosition> copiedCarPositions)
    {
        // Use multiloop starting positions if active
        if (sessionContext.IsMultiloopActive)
        {
            // Multiloop will set the overall starting positions. If these have changed
            // since last time, we need to recalculate the in-class starting positions.
            bool startingPositionsChanged = StartingPositionsChanged();
            if (startingPositionsChanged)
            {
                UpdateMLInClassStartingPositionLookup();
                lastMLOverallStartingPositions.Clear();
                foreach (var kvp in sessionContext.GetStartingPositions())
                {
                    lastMLOverallStartingPositions[kvp.Key] = kvp.Value;
                }
            }

            // Apply the in-class starting positions
            foreach (var cp in copiedCarPositions)
            {
                if (cp.Number == null)
                    continue;
                if (mlInClassStartingPositions.TryGetValue(cp.Number, out var sp))
                    cp.InClassStartingPosition = sp;
                else
                    cp.InClassStartingPosition = 0;
            }
        }
        else // Infer positions from RMonitor data at start of the race
        {
            // Apply starting positions from session context
            foreach (var cp in copiedCarPositions)
            {
                if (cp.Number == null)
                    continue;
                var sp = sessionContext.GetStartingPosition(cp.Number);
                cp.OverallStartingPosition = sp ?? 0;
                var icsp = sessionContext.GetInClassStartingPosition(cp.Number);
                cp.InClassStartingPosition = icsp ?? 0;
            }
        }
    }

    public void Clear()
    {
        positionMetadataProcessor.Clear();
    }

    private bool StartingPositionsChanged()
    {
        var currentStartingPositions = sessionContext.GetStartingPositions();
        if (currentStartingPositions.Count != lastMLOverallStartingPositions.Count)
            return true;
        foreach (var kvp in currentStartingPositions)
        {
            if (!lastMLOverallStartingPositions.TryGetValue(kvp.Key, out var pos) || pos != kvp.Value)
                return true;
        }
        return false;
    }

    private void UpdateMLInClassStartingPositionLookup()
    {
        mlInClassStartingPositions.Clear();
       
        var classGroups = sessionContext.SessionState.CarPositions.GroupBy(x => x.Class);
        foreach (var classGroup in classGroups)
        {
            // Order by overall starting position within each class
            var positions = classGroup.OrderBy(x => x.OverallStartingPosition).ToList();
            for (int i = 0; i < positions.Count; i++)
            {
                var entry = positions[i];
                if (entry.Number != null)
                    mlInClassStartingPositions[entry.Number] = i + 1;
            }
        }
    }
}

/// <summary>
/// Mapper for CarPosition objects using Mapperly code generation
/// </summary>
[Mapper(UseDeepCloning = true)]
public partial class CarPositionMapper
{
    /// <summary>
    /// Creates deep copies of a list of CarPosition objects
    /// </summary>
    public partial List<CarPosition> CloneCarPositions(List<CarPosition> source);

    /// <summary>
    /// Creates a deep copy of a CarPosition object
    /// </summary>
    public partial CarPosition CloneCarPosition(CarPosition source);
}

/// <summary>
/// State change for position metadata updates using CarPositionPatch
/// </summary>
public record PositionMetadataStateUpdate(CarPositionPatch Patch) : ICarStateChange
{
    public string Number => Patch.Number ?? string.Empty;

    public CarPositionPatch? GetChanges(CarPosition state) => Patch;
}
