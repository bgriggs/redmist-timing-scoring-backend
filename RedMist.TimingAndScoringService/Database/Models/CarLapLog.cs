using System.ComponentModel.DataAnnotations;

namespace RedMist.TimingAndScoringService.Database.Models;

public class CarLapLog
{
    [Key]
    public long Id { get; set; }
    [Required]
    public int EventId { get; set; }
    [Required]
    [MaxLength(20)]
    public string CarNumber { get; set; } = string.Empty;
    [Required]
    public DateTime Timestamp { get; set; }
    [Required]
    public int LapNumber { get; set; }
    [Required]
    public int Flag { get; set; }
    [Required]
    [MaxLength(5000)]
    public string LapData { get; set; } = string.Empty;
}
