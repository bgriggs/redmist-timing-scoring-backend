using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Collections.Immutable;

namespace RedMist.TimingAndScoringService.EventStatus.X2.StateChanges;

public record PitStateUpdate(
    string CarNumber,
    Dictionary<string, HashSet<int>> CarLapsWithPitStops,
    Dictionary<uint, Passing> InPit,
    Dictionary<uint, Passing> PitEntrance,
    Dictionary<uint, Passing> PitExit,
    Dictionary<uint, Passing> PitSf,
    Dictionary<uint, Passing> PitOther,
    Dictionary<uint, Passing> Other,
    Dictionary<uint, LoopMetadata> LoopMetadata) : ICarStateChange
{
    public string Number => CarNumber;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch { Number = CarNumber };

        // Set IsInPit based on whether the transponder is in the InPit collection
        // This reflects the actual IsInPit flag from the passing data
        bool newIsInPit = InPit.ContainsKey(state.TransponderId);
        if (state.IsInPit != newIsInPit)
            patch.IsInPit = newIsInPit;

        // Set loop-specific flags based on loop types
        patch.IsEnteredPit = PitEntrance.TryGetValue(state.TransponderId, out _);
        patch.IsExitedPit = PitExit.TryGetValue(state.TransponderId, out _);
        patch.IsPitStartFinish = PitSf.TryGetValue(state.TransponderId, out _);

        // Allow the enter or S/F loop to enable pit flag
        if (!patch.IsInPit ?? false)
            patch.IsInPit = (patch.IsEnteredPit ?? false) || (patch.IsPitStartFinish ?? false);

        if (Other.TryGetValue(state.TransponderId, out var otherPass) &&
            LoopMetadata.TryGetValue(otherPass.Id, out var lm) && state.LastLoopName != lm.Name)
        {
            patch.LastLoopName = lm.Name;
        }
        else
        {
            patch.LastLoopName = string.Empty;
        }

        // Keep track of laps where cars have pitted
        if (patch.IsInPit ?? false && !string.IsNullOrEmpty(patch.Number))
        {
            if (!CarLapsWithPitStops.TryGetValue(patch.Number!, out var laps))
            {
                laps = [];
                CarLapsWithPitStops[patch.Number!] = laps;
            }
            laps.Add(state.LastLapCompleted + 1);
        }

        // Check if the lap was included in a pit stop
        patch.LapIncludedPit = patch.IsInPit;
        if (!patch.IsInPit ?? false && !string.IsNullOrEmpty(patch.Number))
        {
            patch.LapIncludedPit = CarLapsWithPitStops.TryGetValue(patch.Number!, out var laps) && laps.Contains(state.LastLapCompleted);
        }

        return patch;
    }
}
