﻿using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

public class EventStatusLog
{
    [Key]
    public long Id { get; set; }
    [MaxLength(40)]
    public string Type { get; set; } = string.Empty;
    [Required]
    public int EventId { get; set; }
    [Required]
    public int SessionId { get; set; }
    [Required]
    public DateTime Timestamp { get; set; }
    public string Data { get; set; } = string.Empty;
}
