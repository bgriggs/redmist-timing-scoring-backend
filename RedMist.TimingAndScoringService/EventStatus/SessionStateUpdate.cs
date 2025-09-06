namespace RedMist.TimingAndScoringService.EventStatus;

public record SessionStateUpdate(
    List<ISessionStateChange> SessionChanges, List<ICarStateChange> CarChanges);
