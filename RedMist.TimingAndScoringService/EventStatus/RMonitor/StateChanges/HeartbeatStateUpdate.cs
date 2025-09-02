using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record HeartbeatStateUpdate(Heartbeat Heartbeat) : ISessionStateChange
{
    public List<string> Targets => 
    [
        nameof(SessionState.LapsToGo),
        nameof(SessionState.TimeToGo),
        nameof(SessionState.LocalTimeOfDay),
        nameof(SessionState.RunningRaceTime),
        nameof(SessionState.CurrentFlag)
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        state.LapsToGo = Heartbeat.LapsToGo;
        state.TimeToGo = Heartbeat.TimeToGo;
        state.LocalTimeOfDay = Heartbeat.TimeOfDay;
        state.RunningRaceTime = Heartbeat.RaceTime;
        state.CurrentFlag = Heartbeat.FlagStatus.ToFlag();
        return Task.FromResult(true);
    }
}
