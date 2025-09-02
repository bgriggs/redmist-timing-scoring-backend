using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record CarBestLapStateUpdate(PracticeQualifying PracticeQualifying) : ISessionStateChange
{
    public List<string> Targets => 
    [
        nameof(CarPosition.BestLap),
        nameof(CarPosition.BestTime)
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        var c = state.CarPositions.FirstOrDefault(cp => cp.Number == PracticeQualifying.RegistrationNumber);
        if (c != null)
        {
            c.BestLap = PracticeQualifying.BestLap;
            c.BestTime = PracticeQualifying.BestLapTime;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
