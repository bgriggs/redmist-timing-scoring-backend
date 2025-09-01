using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public class PositionInfoStateUpdate(CompletedLap completedLap) : ISessionStateChange
{
    public List<string> Targets => 
    [
        nameof(CarPosition.OverallStartingPosition),
        nameof(CarPosition.LapsLedOverall),
        nameof(CarPosition.CurrentStatus)
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(c => c.Number == completedLap.Number);
        if (c != null)
        {
            c.OverallStartingPosition = completedLap.StartPosition;
            c.LapsLedOverall = completedLap.LapsLed;
            c.CurrentStatus = string.IsNullOrEmpty(completedLap.CurrentStatus)
                ? string.Empty
                : completedLap.CurrentStatus.Length > 12
                    ? completedLap.CurrentStatus[..12]
                    : completedLap.CurrentStatus;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
