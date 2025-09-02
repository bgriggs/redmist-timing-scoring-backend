using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CarLapStateUpdate(RaceInformation RaceInformation) : ISessionStateChange
{
    public List<string> Targets => [nameof(CarPosition.LastLapCompleted)];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(cp => cp.Number == RaceInformation.RegistrationNumber);
        if (c != null)
        {
            c.LastLapCompleted = RaceInformation.Laps;
            c.TotalTime = RaceInformation.RaceTime;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
