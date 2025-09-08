using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using Riok.Mapperly.Abstractions;

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
    public SessionStateUpdate? Process(SessionStateUpdate? rmonitorUpdates)
    {
        if (rmonitorUpdates == null || rmonitorUpdates.CarChanges.Count == 0)
            return null;

        // Make a deep copy of the car positions so that the position processor does not modify the session state.
        var originalCarPositions = sessionContext.SessionState.CarPositions;
        var copiedCarPositions = carPositionMapper.CloneCarPositions(originalCarPositions);

        // Update the copies with position metadata
        positionMetadataProcessor.UpdateCarPositions(copiedCarPositions);

        // Find changes from the original session state CarPositions and the updated ones
        // and create CarPositionPatches to return in a SessionStateUpdate.
        var carChanges = new List<ICarStateChange>();

        for (int i = 0; i < originalCarPositions.Count && i < copiedCarPositions.Count; i++)
        {
            var original = originalCarPositions[i];
            var updated = copiedCarPositions[i];

            var patch = TimingCommon.Models.Mappers.CarPositionMapper.CreatePatch(original, updated);

            if (TimingCommon.Models.Mappers.CarPositionMapper.IsValidPatch(patch))
            {
                patch.Number = original.Number; // Ensure number is set
                var stateChange = new PositionMetadataStateUpdate(patch);
                carChanges.Add(stateChange);
            }
        }

        return carChanges.Count > 0 ? new SessionStateUpdate([], carChanges) : null;
    }

    public void Clear()
    {
        positionMetadataProcessor.Clear();
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
