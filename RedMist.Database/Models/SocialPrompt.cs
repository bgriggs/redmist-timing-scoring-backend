using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

/// <summary>
/// A versioned prompt used to turn an event digest into post copy.
/// </summary>
/// <remarks>
/// Held in the database rather than in source for two reasons. Copy quality is tuned by trial, and a
/// redeploy per wording change makes that loop too slow to actually run; and the voice guide is the
/// one genuinely sensitive part of this pipeline, which keeps the repository able to stay public.
/// <para>
/// Rows are immutable once used: a change is a new <see cref="Version"/>, so the version stamped on
/// a generated post always names the exact text that produced it.
/// </para>
/// </remarks>
public class SocialPrompt
{
    [Key]
    public int Id { get; set; }

    /// <summary>Monotonic per (<see cref="Kind"/>, <see cref="Channel"/>); stamped onto generated posts.</summary>
    public int Version { get; set; }

    public SocialPostKind Kind { get; set; }

    public SocialChannel Channel { get; set; }

    /// <summary>Instructions covering the task and, critically, the rule that only digest facts may be stated.</summary>
    [Required]
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Tone and style guidance, kept separate so voice can be tuned without touching the rules.</summary>
    public string VoiceGuide { get; set; } = string.Empty;

    /// <summary>
    /// Previously approved posts as worked examples, serialized. Feeding accepted copy back in is the
    /// cheapest quality lever available and it compounds as more posts are approved.
    /// </summary>
    public string? FewShotJson { get; set; }

    /// <summary>
    /// Whether this is the version new posts use. At most one row per (Kind, Channel) may be active,
    /// enforced by a filtered unique index rather than by convention.
    /// </summary>
    public bool IsActive { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Why this version was written, so a quality change can be traced to an intent.</summary>
    [MaxLength(500)]
    public string? Notes { get; set; }
}
