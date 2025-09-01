namespace RedMist.TimingAndScoringService.Models;
public record TimingMessage(string Type, string Data, int SessionId, DateTime Timestamp);
