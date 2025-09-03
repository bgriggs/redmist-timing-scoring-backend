namespace RedMist.TimingAndScoringService.EventStatus;

public record SessionStateUpdate(string Source, List<ISessionStateChange> Changes);
