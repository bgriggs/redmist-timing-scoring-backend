using RedMist.Backend.Shared.Models;

namespace RedMist.EventManagement.Models;

/// <summary>
/// Everything running on the site right now, across every organization, for the site operations
/// page.
/// </summary>
/// <remarks>
/// <para>
/// For site administrators only, and unredacted: an event's real name is shown even when it is
/// hidden from the public, with <see cref="SiteOperationsEventDto.HideName"/> and
/// <see cref="SiteOperationsEventDto.IsPrivate"/> carried alongside so the page can say so.
/// </para>
/// <para>
/// Live data only. Every figure here comes from Redis or from the live flags in the database, and
/// disappears when the event is torn down; nothing is kept for later review.
/// </para>
/// <para>
/// Every timestamp is a UTC instant and is written with its "Z".
/// </para>
/// </remarks>
public class SiteOperationsOverviewDto
{
    /// <summary>When this was assembled. Cached for a few seconds, so it can be that much older than the response.</summary>
    public DateTime AsOfUtc { get; set; }

    /// <summary>The active events, by organization name and then newest event first.</summary>
    public List<SiteOperationsEventDto> Events { get; set; } = [];

    public SiteOperationsTotalsDto Totals { get; set; } = new();

    /// <summary>Pods that belong to no event: the shared services.</summary>
    public List<PodHealth> SharedPods { get; set; } = [];

    /// <summary>Event jobs with no pod at all.</summary>
    public List<MissingJob> MissingJobs { get; set; } = [];

    /// <summary>
    /// When the orchestrator last listed the pods, or null when it has not reported within the last
    /// minute. Null means the pod figures are unavailable, not that there are no pods: every pod list
    /// on this page is empty then, and should be read as unknown.
    /// </summary>
    public DateTime? PodsAsOfUtc { get; set; }

    /// <summary>Every relay connected to the relay hub, whether or not it is heartbeating an event.</summary>
    public List<SiteOperationsRelayConnectionDto> Relays { get; set; } = [];
}

/// <summary>One active event.</summary>
/// <remarks>
/// Active means any of: flagged live in the database, a relay heartbeat for it still in Redis -
/// including one gone quiet but not yet past the orchestrator's ten-minute teardown - or pods
/// running for it with neither (<see cref="IsOrphan"/>).
/// </remarks>
public class SiteOperationsEventDto
{
    public int EventId { get; set; }

    public int OrganizationId { get; set; }

    public string OrganizationName { get; set; } = string.Empty;

    public string OrganizationShortName { get; set; } = string.Empty;

    /// <summary>The event's name, unredacted. Empty only for an id with no event row behind it.</summary>
    public string EventName { get; set; } = string.Empty;

    public string? TrackName { get; set; }

    public bool IsPrivate { get; set; }

    public bool HideName { get; set; }

    public bool IsSimulation { get; set; }

    /// <summary>Whether the orchestrator has flagged the event live, which it rewrites every ten seconds.</summary>
    public bool IsLiveInDb { get; set; }

    /// <summary>
    /// Whether the event is archived. An archived event is never live, so one listed here is a relay
    /// heartbeating it or pods still running for it.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>Where the event's timing data comes from: Relay or External.</summary>
    public string TimingSource { get; set; } = string.Empty;

    /// <summary>The event's start date as the organizer entered it: a date, not an instant.</summary>
    public DateTime? EventStartDate { get; set; }

    public SiteOperationsRelayDto Relay { get; set; } = new();

    /// <summary>Pods are running for the event but nothing says it is live: no heartbeat and no live flag.</summary>
    public bool IsOrphan { get; set; }

    /// <summary>The event's pods. Empty when the orchestrator is not reporting; see <see cref="SiteOperationsOverviewDto.PodsAsOfUtc"/>.</summary>
    public List<PodHealth> Pods { get; set; } = [];

    /// <summary>Connections watching right now, or null if they could not be read.</summary>
    public SiteViewerCountsDto? Viewers { get; set; }

    public SiteOperationsMessagesDto Messages { get; set; } = new();
}

/// <summary>The relay feeding one event.</summary>
public class SiteOperationsRelayDto
{
    /// <summary>Whether Redis holds a heartbeat for the event at all.</summary>
    public bool HasHeartbeat { get; set; }

    /// <summary>
    /// When the relay last checked in. A timestamp rather than a connected flag, for the reason given
    /// on <c>EventStatusSummary.RelayLastHeartbeatUtc</c>: what counts as stale belongs to the page.
    /// </summary>
    public DateTime? LastHeartbeatUtc { get; set; }

    public string? RelayVersion { get; set; }

    /// <summary>The hub connection the last heartbeat arrived on.</summary>
    public string? ConnectionId { get; set; }

    /// <summary>
    /// Whether that connection is still registered with the relay hub. False after the relay
    /// disconnected; the heartbeat lingers until the orchestrator's timeout.
    /// </summary>
    public bool ConnectionPresent { get; set; }

    /// <summary>When that connection was opened, when it is still registered.</summary>
    public DateTime? ConnectedUtc { get; set; }
}

/// <summary>Connections watching right now, by client type.</summary>
public class SiteViewerCountsDto
{
    public DateTime AsOfUtc { get; set; }

    public int Total { get; set; }

    /// <summary>
    /// iOS, Android, Web, API and InCar, summing to <see cref="Total"/>. A type with nobody on it is
    /// absent rather than zero. Keys are written exactly as named here, not camel-cased.
    /// </summary>
    public Dictionary<string, int> ByClientType { get; set; } = [];
}

/// <summary>What the relay hub has received for one event.</summary>
/// <remarks>
/// Heartbeats are counted, and listed in <see cref="ByType"/>, but left out of every other figure:
/// a relay heartbeats every ten seconds whether or not its timing feed is alive, so including them
/// would make a dead feed look like a live one.
/// </remarks>
public class SiteOperationsMessagesDto
{
    /// <summary>Messages since the counters were created, heartbeats excluded.</summary>
    public long Total { get; set; }

    /// <summary>When the last message other than a heartbeat arrived.</summary>
    public DateTime? LastMessageUtc { get; set; }

    /// <summary>Messages in the last complete UTC minute, heartbeats excluded.</summary>
    public long PerMinuteLast { get; set; }

    /// <summary>Each type seen, heartbeats included, in presentation order.</summary>
    public List<SiteOperationsMessageTypeDto> ByType { get; set; } = [];

    /// <summary>
    /// Messages per UTC minute, heartbeats excluded, oldest first: the last sixty complete minutes
    /// and then the current minute so far. Minutes with nothing in them are zeros, not gaps.
    /// </summary>
    public List<SiteOperationsMinuteDto> PerMinute { get; set; } = [];
}

public class SiteOperationsMessageTypeDto
{
    public string Type { get; set; } = string.Empty;

    public long Total { get; set; }

    public DateTime? LastUtc { get; set; }

    /// <summary>This type's count in the last complete UTC minute.</summary>
    public long PerMinuteLast { get; set; }
}

public class SiteOperationsMinuteDto
{
    public DateTime MinuteUtc { get; set; }

    public long Count { get; set; }
}

public class SiteOperationsTotalsDto
{
    /// <summary>Active events, orphans included.</summary>
    public int Events { get; set; }

    /// <summary>Viewers summed across the events whose counts could be read.</summary>
    public SiteViewerCountsDto Viewers { get; set; } = new();

    /// <summary>
    /// Relay hub connections that a current heartbeat points at. A connection heartbeating nothing - a
    /// relay with no event picked, or a record left behind by a relay hub pod that died - is listed in
    /// the relays but not counted here, because the two cannot be told apart and the dead records
    /// never go away on their own.
    /// </summary>
    public int RelaysConnected { get; set; }

    /// <summary>
    /// Pods, event and shared, that are not ready and have not simply finished, or that have a
    /// container waiting on something.
    /// </summary>
    public int UnhealthyPods { get; set; }
}

/// <summary>One relay hub connection.</summary>
/// <remarks>
/// Connection records have no expiry, so one left behind by a relay hub pod that died without
/// running its disconnect handler stays listed. A connection with no heartbeat events is either a
/// relay with no event selected or one of those.
/// </remarks>
public class SiteOperationsRelayConnectionDto
{
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>The client id the relay authenticated as.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The organization that client id belongs to, or null if none does.</summary>
    public int? OrganizationId { get; set; }

    public string? OrganizationName { get; set; }

    public DateTime ConnectedUtc { get; set; }

    /// <summary>The events whose latest heartbeat arrived on this connection.</summary>
    public List<int> HeartbeatEventIds { get; set; } = [];
}

/// <summary>
/// Concurrent connections summed across every active event, in one-minute buckets.
/// </summary>
/// <remarks>
/// Computed by the same sweep as one event's live viewership, over every active event's intervals
/// together, so a connection is counted once whichever event it is watching and the peak is a
/// moment that actually happened rather than the sum of peaks at different times.
/// </remarks>
public class OverallViewershipDto
{
    public DateTime AsOfUtc { get; set; }

    /// <summary>The width of every bucket, in seconds.</summary>
    public int BucketSeconds { get; set; }

    /// <summary>
    /// Where the series begins: the earliest of the events' own live viewership window starts, but
    /// never more than twenty-four hours ago. The series runs from here to <see cref="AsOfUtc"/>.
    /// </summary>
    public DateTime WindowStartUtc { get; set; }

    /// <summary>The events summed. Orphans are not included.</summary>
    public List<int> EventIds { get; set; } = [];

    /// <summary>The most connections held at once, across all of them, for a non-zero length of time.</summary>
    public int MaxConcurrent { get; set; }

    /// <summary>Start of the first bucket that reached <see cref="MaxConcurrent"/>, or null when nobody watched.</summary>
    public DateTime? PeakUtc { get; set; }

    /// <summary>The series, oldest first, with the same bucket semantics as a live session's.</summary>
    public List<LiveViewershipBucketDto> Buckets { get; set; } = [];
}
