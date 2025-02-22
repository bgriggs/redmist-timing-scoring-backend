using System.ComponentModel.DataAnnotations;

namespace RedMist.TimingAndScoringService.Database.Models;

public class Event
{
    [Key]
    public int Id { get; set; }
    [Required]
    public int OrganizationId { get; set; }
    public int EventReferenceId { get; set; }
    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public bool EnableSourceDataLogging { get; set; } = true;
}
