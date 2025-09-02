using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record SessionStateUpdated(int SessionId, string SessionName) : ISessionStateChange
{
    public List<string> Targets => 
    [
        nameof(SessionState.SessionId),
        nameof(SessionState.SessionName)
    ];

    public Task<bool> ApplyToState(SessionState state)
    {
        state.SessionId = SessionId;
        state.SessionName = SessionName;
        return Task.FromResult(true);
    }
}
