namespace RedMist.TimingAndScoringService.EventStatus;

public record SessionStateUpdate(string source, List<ISessionStateChange> changes);
