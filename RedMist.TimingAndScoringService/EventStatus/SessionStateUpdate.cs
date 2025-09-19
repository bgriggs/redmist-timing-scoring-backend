using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

public class SessionStateUpdate(
    List<ISessionStateChange> sessionChanges, List<ICarStateChange> carChanges)
{
    public List<ISessionStateChange> SessionChanges { get; } = sessionChanges;
    public List<ICarStateChange> CarChanges { get; } = carChanges;    
}
