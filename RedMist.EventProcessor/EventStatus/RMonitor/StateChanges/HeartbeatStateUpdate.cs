using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;

/// <param name="Heartbeat">Parsed $F heartbeat.</param>
/// <param name="SuppressFlag">
/// Skip the flag portion of the heartbeat, e.g. while Flagtronics is the active flag source.
/// </param>
public record HeartbeatStateUpdate(Heartbeat Heartbeat, bool SuppressFlag = false) : ISessionStateChange
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

        if (!SuppressFlag)
        {
            var f = Heartbeat.FlagStatus.ToFlag();
            if (state.CurrentFlag != f)
                patch.CurrentFlag = f;
        }

        return patch;
    }
}
