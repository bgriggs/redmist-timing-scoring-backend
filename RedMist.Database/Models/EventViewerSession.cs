using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

/// <summary>
/// One client's stretch of watching one event: from the moment it subscribed to the moment it
/// stopped. The raw material every viewership number is computed from.
/// </summary>
/// <remarks>
/// <para>
/// A row is a <b>connection</b>, not a person. A phone that changes network or is backgrounded drops
/// its SignalR connection and opens a new one, so one viewer routinely produces many rows over a
/// session. Concurrency is unaffected by that churn - a reconnect closes one row and opens another -
/// but the row count is not a viewer count and must never be presented as one. <see cref="InstallId"/>
/// exists for the day a client sends a stable per-install identifier and that becomes answerable.
/// </para>
/// <para>
/// Which racing session a row belongs to is deliberately not stored. Clients subscribe to an event,
/// never to a session, so attribution is done at report time by intersecting this row's interval with
/// each session's window - which also gives the right answer for a viewer who watches across a
/// session boundary, and leaves the rows correct if a session's start or end time is later corrected.
/// </para>
/// </remarks>
public class EventViewerSession
{
    public long Id { get; set; }

    public int EventId { get; set; }

    /// <summary>The SignalR connection id. Unique only while the connection is alive.</summary>
    [MaxLength(64)]
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>iOS, Android, Web or API, as resolved by ClientTypeHelper from the token's azp claim.</summary>
    [MaxLength(16)]
    public string ClientType { get; set; } = string.Empty;

    public DateTime StartUtc { get; set; }

    /// <summary>Null while the session is still open. Closed rows are what the report reads.</summary>
    public DateTime? EndUtc { get; set; }

    public ViewerSessionEndReason? EndReason { get; set; }

    /// <summary>
    /// True when the start was reconstructed by the reconciler from the live connection hash rather
    /// than observed on the stream, so a report can say what fraction of its input was inferred.
    /// </summary>
    public bool StartInferred { get; set; }

    /// <summary>Whether the viewer was in in-car driver mode rather than watching the timing screen.</summary>
    public bool IsInCar { get; set; }

    [MaxLength(CarNumberMaxLength)]
    public string? CarNumber { get; set; }

    /// <summary>The column width of <see cref="CarNumber"/>.</summary>
    public const int CarNumberMaxLength = 16;

    /// <summary>
    /// A car number cut to fit the column, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The car number comes from whatever a driver typed into the app, and nothing limits it on the
    /// way in. An over-long one used to be rejected by PostgreSQL, and both writers of this table
    /// save in batches whose failure handling discards every row added in the pass - so one phone
    /// with a long car number cost the event every other session recorded alongside it, on every
    /// pass, for as long as it stayed connected.
    ///
    /// Cut here rather than where the number enters the hub, because there it also names the
    /// driver's in-car group, and shortening it would put them in a group nobody is publishing to.
    /// </remarks>
    public static string? FitCarNumber(string? carNumber)
        => carNumber is { Length: > CarNumberMaxLength } ? carNumber[..CarNumberMaxLength] : carNumber;

    /// <summary>
    /// Reserved for a stable per-install identifier supplied by the client. Nothing populates it yet;
    /// the column exists so adding it later is a client change with no second migration. Whatever
    /// fills it is pseudonymous and will need a retention policy.
    /// </summary>
    [MaxLength(64)]
    public string? InstallId { get; set; }
}
