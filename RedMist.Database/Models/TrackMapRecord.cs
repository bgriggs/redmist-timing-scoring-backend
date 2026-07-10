using RedMist.TimingCommon.Models;

namespace RedMist.Database.Models;

/// <summary>
/// Persists the learned <see cref="TrackMap"/> for an event so the GPS lap-time projection and other
/// consumers can reuse it across sessions and service restarts without re-learning the track each time.
/// One row per event; the map itself is stored as JSONB.
/// </summary>
public class TrackMapRecord
{
    /// <summary>Event the map belongs to (primary key — one map per event).</summary>
    public int EventId { get; set; }

    /// <summary>The learned track map (centerline points + cumulative distances), stored as JSONB.</summary>
    public TrackMap Map { get; set; } = new();

    /// <summary>When the map was last built/updated, in UTC.</summary>
    public DateTime UpdatedUtc { get; set; }
}