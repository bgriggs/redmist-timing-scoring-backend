using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

/// <summary>
/// Raw, opaque messages received from an external timing source, logged for replay and
/// troubleshooting. The <see cref="Data"/> payload is stored verbatim and is never interpreted by
/// the backend — its format is private to the external source's ingestor. A single row holds a
/// batch of one or more source messages (e.g. an NDJSON chunk) so high-rate feeds don't produce a
/// row per frame.
/// </summary>
public class ExternalMessageLog
{
    [Key]
    public long Id { get; set; }
    [MaxLength(20)]
    public string Type { get; set; } = string.Empty;
    [Required]
    public int EventId { get; set; }
    [Required]
    public int SessionId { get; set; }
    [Required]
    public DateTime Timestamp { get; set; }
    public string Data { get; set; } = string.Empty;
}
