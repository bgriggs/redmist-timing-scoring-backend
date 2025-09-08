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

        if (InPit.TryGetValue(state.TransponderId, out _) && !state.IsInPit)
        {
            patch.IsInPit = true;
        }
        else if (state.IsInPit)
        {
            patch.IsInPit = false;
        }

        if (PitEntrance.TryGetValue(state.TransponderId, out _) && !state.IsEnteredPit)
        {
            patch.IsEnteredPit = true;

            if (!state.IsInPit)
                patch.IsInPit = true;
        }

        if (PitExit.TryGetValue(state.TransponderId, out _) && !state.IsExitedPit)
        {
            patch.IsExitedPit = true;
            if (state.IsInPit)
                patch.IsInPit = false;
        }

        if (PitSf.TryGetValue(state.TransponderId, out _) && !state.IsPitStartFinish)
        {
            patch.IsPitStartFinish = true;
            if (!state.IsInPit)
                patch.IsInPit = true;
        }

        if (PitOther.TryGetValue(state.TransponderId, out _) && !state.IsInPit)
        {
            patch.IsInPit = true;
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
