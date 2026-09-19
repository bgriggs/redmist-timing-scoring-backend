using Prometheus;

namespace RedMist.EventLogger.Services;

/// <summary>
/// Viewership capture metrics, shared by the stream consumer and the reconciler.
/// </summary>
/// <remarks>
/// These are the only way to tell whether the numbers an event produces can be trusted, because both
/// failure modes are silent in the data itself. A large <c>ReconciledAbsent</c> share of the closes
/// means status API pods are dying without running their disconnect handler; a large
/// <c>CappedDuration</c> share means the live connection hash is going stale and the duration cap is
/// the only thing bounding the count; a large inferred share of the starts means the stream is losing
/// entries before the logger reads them.
/// </remarks>
internal static class ViewerSessionMetrics
{
    public static readonly Gauge Open = Metrics.CreateGauge(
        "viewer_sessions_open", "Viewer sessions currently open for this event");

    public static readonly Counter Started = Metrics.CreateCounter(
        "viewer_sessions_started_total", "Viewer sessions opened",
        new CounterConfiguration { LabelNames = ["client_type", "inferred"] });

    public static readonly Counter Closed = Metrics.CreateCounter(
        "viewer_sessions_closed_total", "Viewer sessions closed",
        new CounterConfiguration { LabelNames = ["reason"] });
}
