using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

public class Sponsor
{
    public int Id { get; set; }
    [MaxLength(100)]
    public required string Name { get; set; }
    [MaxLength(2083)]
    public required string ImageUrl { get; set; }
    [MaxLength(2083)]
    public required string TargetUrl { get; set; }
    [MaxLength(256)]
    public string? AltText { get; set; } = string.Empty;
    public int DisplayDurationMs { get; set; }
    public int DisplayPriority { get; set; }
    [MaxLength(128)]
    public string ContactName { get; set; } = string.Empty;
    [MaxLength(254)]
    public string ContactEmail { get; set; } = string.Empty;
    [MaxLength(20)]
    public string ContactPhone { get; set; } = string.Empty;
    [MaxLength(7)]
    public string Amount { get; set; } = string.Empty;
    public DateOnly SubscriptionStart { get; set; }
    public DateOnly? SubscriptionEnd { get; set; }
}
