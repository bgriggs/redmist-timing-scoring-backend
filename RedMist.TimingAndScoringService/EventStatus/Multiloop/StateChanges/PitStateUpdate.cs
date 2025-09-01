using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public class PitStateUpdate(CompletedLap completedLap) : ISessionStateChange
{
    public List<string> Targets => 
    [
        nameof(CarPosition.LastLapPitted),
        nameof(CarPosition.PitStopCount)
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(c => c.Number == completedLap.Number);
        if (c != null)
        {
            c.LastLapPitted = completedLap.LastLapPitted;
            c.PitStopCount = completedLap.PitStopCount;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
