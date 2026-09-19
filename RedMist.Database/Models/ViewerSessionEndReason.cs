using System.Text.Json.Serialization;

namespace RedMist.Database.Models;

/// <summary>
/// Why a viewer session was closed.
/// </summary>
/// <remarks>
/// Worth recording rather than inferring, because the mix is the only signal that says whether the
/// numbers can be trusted. A run dominated by <see cref="ReconciledAbsent"/> means status API pods
/// are dying without running their disconnect handler; a rising <see cref="CappedDuration"/> share
/// means the live connection hash is going stale and the cap is the only thing bounding the count.
/// <para>
/// Serialized as its name both on the wire and in the database, so a member can be added without
/// renumbering anything already written.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewerSessionEndReason
{
    /// <summary>The client explicitly unsubscribed from the event.</summary>
    Unsubscribed,

    /// <summary>The connection dropped and the hub's disconnect handler ran.</summary>
    Disconnected,

    /// <summary>The client moved this connection to a different event.</summary>
    Switched,

    /// <summary>
    /// The connection was no longer in the live connection hash. The end time is when it was first
    /// seen missing, not when the reconciler acted.
    /// </summary>
    ReconciledAbsent,

    /// <summary>
    /// The session outlived the maximum plausible duration and was closed at the cap. This is the
    /// only bound on a session whose hash entry was orphaned by a hard pod kill.
    /// </summary>
    CappedDuration,

    /// <summary>The event was torn down while the session was still open.</summary>
    EventTeardown,
}
