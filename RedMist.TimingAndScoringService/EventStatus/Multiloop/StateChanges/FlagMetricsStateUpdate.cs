using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public class FlagMetricsStateUpdate(FlagInformation flagInformation) : ISessionStateChange
{
    public List<string> Targets =>
    [
        nameof(SessionState.GreenTimeMs),
        nameof(SessionState.GreenLaps),
        nameof(SessionState.YellowTimeMs),
        nameof(SessionState.YellowLaps),
        nameof(SessionState.NumberOfYellows),
        nameof(SessionState.RedTimeMs),
        nameof(SessionState.AverageRaceSpeed),
        nameof(SessionState.LeadChanges),
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        state.GreenTimeMs = (int)flagInformation.GreenTime.TotalMilliseconds;
        state.GreenLaps = flagInformation.GreenLaps;
        state.YellowTimeMs = (int)flagInformation.YellowTime.TotalMilliseconds;
        state.YellowLaps = flagInformation.YellowLaps;
        state.NumberOfYellows = flagInformation.NumberOfYellows;
        state.RedTimeMs = (int)flagInformation.RedTime.TotalMilliseconds;
        state.AverageRaceSpeed = flagInformation.AverageRaceSpeedMph;
        state.LeadChanges = flagInformation.LeadChanges;
        return Task.FromResult(true);
    }
}
