using RedMist.TimingCommon.Models;

namespace RedMist.ControlLogs.Announcements;

/// <summary>
/// Thread-safe singleton store holding the latest announcement-derived control log entries. Registered
/// as a singleton so the stream consumer and the control log poll share the same instance.
/// </summary>
public class AnnouncementControlLogStore : IAnnouncementControlLogStore
{
    private volatile IReadOnlyList<ControlLogEntry> entries = [];

    public void Set(IReadOnlyList<ControlLogEntry> entries) => this.entries = entries ?? [];

    public IReadOnlyList<ControlLogEntry> Get() => entries;
}
