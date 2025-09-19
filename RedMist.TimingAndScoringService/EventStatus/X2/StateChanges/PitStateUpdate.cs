using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Collections.Immutable;

namespace RedMist.TimingAndScoringService.EventStatus.X2.StateChanges;

public record PitStateUpdate(
    string CarNumber,
    Dictionary<string, HashSet<int>> CarLapsWithPitStops,
    ImmutableDictionary<uint, Passing> InPit,
    ImmutableDictionary<uint, Passing> PitEntrance,
    ImmutableDictionary<uint, Passing> PitExit,
    ImmutableDictionary<uint, Passing> PitSf,
    ImmutableDictionary<uint, Passing> PitOther,
    ImmutableDictionary<uint, Passing> Other,
    ImmutableDictionary<uint, LoopMetadata> LoopMetadata) : ICarStateChange
{
    public string Number => CarNumber;

    public CarPositionPatch? GetChanges(CarPosition state)
    {
        var patch = new CarPositionPatch { Number = CarNumber };

        // Set IsInPit based on whether the transponder is in the InPit collection
        // This reflects the actual IsInPit flag from the passing data
        bool newIsInPit = InPit.ContainsKey(state.TransponderId);
        if (state.IsInPit != newIsInPit)
        {
            patch.IsInPit = newIsInPit;
        }

        // Set loop-specific flags based on loop types
        if (PitEntrance.TryGetValue(state.TransponderId, out _) && !state.IsEnteredPit)
        {
            patch.IsEnteredPit = true;
        }

        if (PitExit.TryGetValue(state.TransponderId, out _) && !state.IsExitedPit)
        {
            patch.IsExitedPit = true;
        }

        if (PitSf.TryGetValue(state.TransponderId, out _) && !state.IsPitStartFinish)
        {
            patch.IsPitStartFinish = true;
        }

        if (Other.TryGetValue(state.TransponderId, out var otherPass) &&
            LoopMetadata.TryGetValue(otherPass.Id, out var lm) && state.LastLoopName != lm.Name)
        {
            patch.LastLoopName = lm.Name;
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
