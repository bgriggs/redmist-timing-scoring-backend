using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

public class SessionContext
{
    public SessionState SessionState { get; } = new SessionState();

    public virtual bool IsMultiloopActive { get; set; }
}
