using Microsoft.EntityFrameworkCore;
using RedMist.TimingCommon.Models;
using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

[PrimaryKey(nameof(EventId), nameof(SessionId), nameof(Flag), nameof(StartTime))]
public class FlagLog
{
    [Required]
    public int EventId { get; set; }
    [Required]
    public int SessionId { get; set; }
    [Required]
    public Flags Flag { get; set; }
    [Required]
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}
