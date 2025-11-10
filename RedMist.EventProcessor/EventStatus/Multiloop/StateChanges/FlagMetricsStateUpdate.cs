using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.Multiloop.StateChanges;

public record FlagMetricsStateUpdate(FlagInformation FlagInformation) : ISessionStateChange
{
    public SessionStatePatch? GetChanges(SessionState state)
    {
        var patch = new SessionStatePatch { SessionId = state.SessionId };
        if (state.GreenTimeMs != (int)FlagInformation.GreenTime.TotalMilliseconds)
            patch.GreenTimeMs = (int)FlagInformation.GreenTime.TotalMilliseconds;
        if (state.GreenLaps != FlagInformation.GreenLaps)
            patch.GreenLaps = FlagInformation.GreenLaps;
        if (state.YellowTimeMs != (int)FlagInformation.YellowTime.TotalMilliseconds)
            patch.YellowTimeMs = (int)FlagInformation.YellowTime.TotalMilliseconds;
        if (state.YellowLaps != FlagInformation.YellowLaps)
            patch.YellowLaps = FlagInformation.YellowLaps;
        if (state.NumberOfYellows != FlagInformation.NumberOfYellows)
            patch.NumberOfYellows = FlagInformation.NumberOfYellows;
        if (state.RedTimeMs != (int)FlagInformation.RedTime.TotalMilliseconds)
            patch.RedTimeMs = (int)FlagInformation.RedTime.TotalMilliseconds;
        if (state.AverageRaceSpeed != FlagInformation.AverageRaceSpeedMph)
            patch.AverageRaceSpeed = FlagInformation.AverageRaceSpeedMph;
        if (state.LeadChanges != FlagInformation.LeadChanges)
            patch.LeadChanges = FlagInformation.LeadChanges;

        return patch;
    }
}
