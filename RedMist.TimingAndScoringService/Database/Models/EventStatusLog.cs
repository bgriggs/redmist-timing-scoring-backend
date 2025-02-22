using System.ComponentModel.DataAnnotations;

namespace RedMist.TimingAndScoringService.Database.Models;

public class EventStatusLog
{
    [Key]
    public long Id { get; set; }
    [Required]
    public int EventId { get; set; }
    [Required]
    public DateTime Timestamp { get; set; }
    public string Data { get; set; } = string.Empty;
}
