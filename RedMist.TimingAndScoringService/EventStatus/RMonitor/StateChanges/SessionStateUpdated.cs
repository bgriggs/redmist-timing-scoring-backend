using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;

public record SessionStateUpdated(int SessionId, string SessionName) : ISessionStateChange
{
    public SessionStatePatch? GetChanges(SessionState state)
    {
        if (state.SessionId == SessionId && state.SessionName == SessionName)
        {
            return null;
        }

        return new SessionStatePatch
        {
            SessionId = SessionId,
            SessionName = SessionName
        };
    }
}
