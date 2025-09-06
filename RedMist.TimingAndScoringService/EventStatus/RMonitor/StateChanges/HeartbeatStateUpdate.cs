using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record HeartbeatStateUpdate(Heartbeat Heartbeat) : ISessionStateChange
{
    public SessionStatePatch? GetChanges(SessionState state)
    {
        var patch = new SessionStatePatch();

        if (state.LapsToGo != Heartbeat.LapsToGo)
            patch.LapsToGo = Heartbeat.LapsToGo;
        if (state.TimeToGo != Heartbeat.TimeToGo)
            patch.TimeToGo = Heartbeat.TimeToGo;
        if (state.LocalTimeOfDay != Heartbeat.TimeOfDay)
            patch.LocalTimeOfDay = Heartbeat.TimeOfDay;
        if (state.RunningRaceTime != Heartbeat.RaceTime)
            patch.RunningRaceTime = Heartbeat.RaceTime;
        var f = Heartbeat.FlagStatus.ToFlag();
        if (state.CurrentFlag != f)
            patch.CurrentFlag = f;

        return patch;
    }
}
