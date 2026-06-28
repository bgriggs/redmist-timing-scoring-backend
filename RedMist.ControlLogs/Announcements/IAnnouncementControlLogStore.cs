using RedMist.TimingCommon.Models;

namespace RedMist.ControlLogs.Announcements;

/// <summary>
/// In-memory hand-off between the push-based announcement stream consumer and the poll-based control
/// log pipeline. The consumer writes the latest parsed entries; <see cref="AnnouncementControlLog"/>
/// reads them on each poll. Implementations must be thread-safe.
/// </summary>
public interface IAnnouncementControlLogStore
{
    /// <summary>Replaces the current set of entries with the latest full announcement-derived list.</summary>
    void Set(IReadOnlyList<ControlLogEntry> entries);

    /// <summary>A snapshot of the current entries.</summary>
    IReadOnlyList<ControlLogEntry> Get();
}
