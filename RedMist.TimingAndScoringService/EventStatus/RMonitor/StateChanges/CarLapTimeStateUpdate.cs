using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CarLapTimeStateUpdate(PassingInformation PassingInformation) : ISessionStateChange
{
    public List<string> Targets =>
    [
        nameof(CarPosition.LastLapTime),
        nameof(CarPosition.TotalTime)
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(cp => cp.Number == PassingInformation.RegistrationNumber);
        if (c != null)
        {
            c.LastLapTime = PassingInformation.LapTime;
            c.TotalTime = PassingInformation.RaceTime;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
