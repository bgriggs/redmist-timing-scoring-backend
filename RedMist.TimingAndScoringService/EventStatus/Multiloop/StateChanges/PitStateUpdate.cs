using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record PitStateUpdate(CompletedLap CompletedLap) : ISessionStateChange
{
    public List<string> Targets => 
    [
        nameof(CarPosition.LastLapPitted),
        nameof(CarPosition.PitStopCount)
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(c => c.Number == CompletedLap.Number);
        if (c != null)
        {
            c.LastLapPitted = CompletedLap.LastLapPitted;
            c.PitStopCount = CompletedLap.PitStopCount;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
