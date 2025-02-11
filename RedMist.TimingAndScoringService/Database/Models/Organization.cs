using System.ComponentModel.DataAnnotations;

namespace RedMist.TimingAndScoringService.Database.Models;

public class Organization
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;
}
