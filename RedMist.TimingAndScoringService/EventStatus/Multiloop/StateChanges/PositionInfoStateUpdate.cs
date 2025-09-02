using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record PositionInfoStateUpdate(CompletedLap CompletedLap) : ISessionStateChange
{
    public List<string> Targets => 
    [
        nameof(CarPosition.OverallStartingPosition),
        nameof(CarPosition.LapsLedOverall),
        nameof(CarPosition.CurrentStatus)
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(c => c.Number == CompletedLap.Number);
        if (c != null)
        {
            c.OverallStartingPosition = CompletedLap.StartPosition;
            c.LapsLedOverall = CompletedLap.LapsLed;
            c.CurrentStatus = string.IsNullOrEmpty(CompletedLap.CurrentStatus)
                ? string.Empty
                : CompletedLap.CurrentStatus.Length > 12
                    ? CompletedLap.CurrentStatus[..12]
                    : CompletedLap.CurrentStatus;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
