using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;

public class SessionResetUpdate : ISessionStateChange
{
    public SessionStatePatch? GetChanges(SessionState state)
    {
        return null;
    }
}
