using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;

/// <param name="Heartbeat">Parsed $F heartbeat.</param>
/// <param name="TrackFlag">
/// The overall track flag to apply, already resolved against any Flagtronics override. When
/// null, the heartbeat's own parsed flag is used (the plain RMonitor case).
/// </param>
public record HeartbeatStateUpdate(Heartbeat Heartbeat, Flags? TrackFlag = null) : ISessionStateChange
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

        var f = TrackFlag ?? Heartbeat.FlagStatus.ToFlag();
        if (state.CurrentFlag != f)
            patch.CurrentFlag = f;

        return patch;
    }
}
