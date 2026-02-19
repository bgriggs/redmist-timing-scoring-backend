using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

public class SponsorTelemetryLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    [MaxLength(200)]
    public required string Source { get; set; }
    [MaxLength(50)]
    public required string EventId { get; set; }
    [MaxLength(200)]
    public required string ImageId { get; set; }
    [MaxLength(30)]
    public required string EventType { get; set; }
    public int? DurationMs { get; set; }
}
