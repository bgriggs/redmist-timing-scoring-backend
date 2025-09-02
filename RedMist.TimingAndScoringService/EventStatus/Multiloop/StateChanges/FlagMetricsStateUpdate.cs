using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;

public record FlagMetricsStateUpdate(FlagInformation FlagInformation) : ISessionStateChange
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
        state.GreenTimeMs = (int)FlagInformation.GreenTime.TotalMilliseconds;
        state.GreenLaps = FlagInformation.GreenLaps;
        state.YellowTimeMs = (int)FlagInformation.YellowTime.TotalMilliseconds;
        state.YellowLaps = FlagInformation.YellowLaps;
        state.NumberOfYellows = FlagInformation.NumberOfYellows;
        state.RedTimeMs = (int)FlagInformation.RedTime.TotalMilliseconds;
        state.AverageRaceSpeed = FlagInformation.AverageRaceSpeedMph;
        state.LeadChanges = FlagInformation.LeadChanges;
        return Task.FromResult(true);
    }
}
