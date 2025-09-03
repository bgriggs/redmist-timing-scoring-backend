using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Collections.Immutable;

namespace RedMist.TimingAndScoringService.EventStatus.X2.StateChanges;

public record PitStateUpdate(
    Dictionary<string, HashSet<int>> CarLapsWithPitStops,
    ImmutableDictionary<uint, Passing> InPit,
    ImmutableDictionary<uint, Passing> PitEntrance,
    ImmutableDictionary<uint, Passing> PitExit,
    ImmutableDictionary<uint, Passing> PitSf,
    ImmutableDictionary<uint, Passing> PitOther,
    ImmutableDictionary<uint, Passing> Other,
    ImmutableDictionary<uint, LoopMetadata> LoopMetadata) : ISessionStateChange
{
    public List<string> Targets => 
    [
        nameof(CarPosition.IsInPit),
        nameof(CarPosition.IsEnteredPit),
        nameof(CarPosition.IsPitStartFinish),
        nameof(CarPosition.IsExitedPit),
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        foreach (var pos in state.CarPositions)
        {
            //ClearPositionLoopData(pos);

            if (InPit.TryGetValue(pos.TransponderId, out _))
            {
                pos.IsInPit = true;
            }

            if (PitEntrance.TryGetValue(pos.TransponderId, out _))
            {
                pos.IsEnteredPit = true;
                pos.IsInPit = true;
            }

            if (PitExit.TryGetValue(pos.TransponderId, out _))
            {
                pos.IsExitedPit = true;
                pos.IsInPit = true;
            }

            if (PitSf.TryGetValue(pos.TransponderId, out _))
            {
                pos.IsPitStartFinish = true;
                pos.IsInPit = true;
            }

            if (PitOther.TryGetValue(pos.TransponderId, out _))
            {
                pos.IsInPit = true;
            }

            if (Other.TryGetValue(pos.TransponderId, out var otherPass) && LoopMetadata.TryGetValue(otherPass.Id, out var lm))
            {
                pos.LastLoopName = lm.Name;
            }

            // Keep track of laps where cars have pitted
            if (pos.IsInPit && !string.IsNullOrEmpty(pos.Number))
            {
                if (!CarLapsWithPitStops.TryGetValue(pos.Number, out var laps))
                {
                    laps = [];
                    CarLapsWithPitStops[pos.Number] = laps;
                }
                laps.Add(pos.LastLapCompleted + 1);
            }

            // Check if the lap was included in a pit stop
            pos.LapIncludedPit = pos.IsInPit;
            if (!pos.IsInPit && !string.IsNullOrEmpty(pos.Number))
            {
                pos.LapIncludedPit = CarLapsWithPitStops.TryGetValue(pos.Number, out var laps) && laps.Contains(pos.LastLapCompleted);
            }
        }

        return Task.FromResult(true);
    }
}
