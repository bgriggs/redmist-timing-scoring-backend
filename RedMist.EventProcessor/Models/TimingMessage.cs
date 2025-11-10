namespace RedMist.EventProcessor.Models;
public record TimingMessage(string Type, string Data, int SessionId, DateTime Timestamp);
