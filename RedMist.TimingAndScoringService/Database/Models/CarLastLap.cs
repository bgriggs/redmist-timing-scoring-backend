using System.ComponentModel.DataAnnotations;

namespace RedMist.TimingAndScoringService.Database.Models;

/// <summary>
/// Book keeping table for the last lap of each car in an event.
/// Used to restore lap tracking on a service restart.
/// </summary>
public class CarLastLap
{
    public int Id { get; set; }
    public int EventId { get; set; }
    [MaxLength(20)]
    public string CarNumber { get; set; } = string.Empty;
    public int LastLapNumber { get; set; }
    public DateTime LastLapTimestamp { get; set; }
}
