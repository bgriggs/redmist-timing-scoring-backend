using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

public class RelayLog
{
    public long Id { get; set; }
    public int OrganizationId { get; set; }
    public DateTime Timestamp { get; set; }
    [MaxLength(20)]
    public required string Level { get; set; }
    [MaxLength(300)]
    public required string State { get; set; }
    [MaxLength(1024)]
    public required string Exception { get; set; }
}
