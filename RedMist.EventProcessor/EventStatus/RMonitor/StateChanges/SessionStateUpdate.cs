using RedMist.Backend.Shared.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;

public record SessionStateUpdate(int SessionId, string SessionName) : ISessionStateChange
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
            SessionName = SessionName,
            IsPracticeQualifying = SessionHelper.IsPracticeOrQualifyingSession(SessionName)
        };
    }
}
