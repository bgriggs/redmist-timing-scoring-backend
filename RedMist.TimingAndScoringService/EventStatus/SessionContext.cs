using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Holds context information shared across the processing pipeline.
/// </summary>
public class SessionContext
{
    public SessionState SessionState { get; } = new SessionState();

    public virtual CancellationToken CancellationToken { get; set; }
    public virtual bool IsMultiloopActive { get; set; }
}
