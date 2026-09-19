using RedMist.Database.Models;
using RedMist.PostEventReports.Sections.Viewership;

namespace RedMist.PostEventReports.Email;

/// <summary>One row of a rendered chart: the stacked averages, and the peak beside them.</summary>
/// <param name="StartUtc">Start of the interval the row covers.</param>
/// <param name="AveragesByType">Mean concurrency per client type, in <see cref="ViewerClientTypes.Reported"/> order.</param>
/// <param name="Max">Highest concurrency anywhere in the interval.</param>
public readonly record struct ChartRow(DateTime StartUtc, double[] AveragesByType, int Max)
{
    public double Total => AveragesByType.Sum();
}

/// <summary>A chart ready to render.</summary>
/// <param name="Rows">The rows, in time order.</param>
/// <param name="Interval">The width of each row.</param>
/// <param name="Scale">The value the widest bar represents.</param>
public sealed record Chart(IReadOnlyList<ChartRow> Rows, TimeSpan Interval, double Scale);

/// <summary>
/// Turns stored fifteen-minute buckets into a chart short enough to put in an email.
/// </summary>
/// <remarks>
/// <para>
/// A twenty-four hour race is ninety-six buckets. Two reasons that cannot go in a message as it
/// stands: nobody reads ninety-six rows, and Gmail clips a message over roughly 102 KB, which
/// ninety-six rows of nested tables with their inline styles will exceed. So the chart - and only the
/// chart - is widened until it fits in <see cref="MaxRows"/>.
/// </para>
/// <para>
/// The stored buckets stay at fifteen minutes regardless, so the detail is still there for anything
/// that reads them later.
/// </para>
/// </remarks>
public static class ChartRollup
{
    /// <summary>The most rows any one chart may have before it is widened.</summary>
    public const int MaxRows = 24;

    /// <summary>
    /// The most chart rows the whole report may contain.
    /// </summary>
    /// <remarks>
    /// A per-chart limit is not enough on its own. An endurance weekend with ten sessions produces
    /// eleven charts, each within its own limit and the report as a whole far past Gmail's hundred
    /// kilobyte clipping threshold - at which point the reader sees "[Message clipped]" and however
    /// much of the report happened to fit. This budget is what the renderer divides between them.
    /// </remarks>
    public const int MaxRowsPerReport = 40;

    /// <summary>The fewest rows worth drawing a chart with at all.</summary>
    public const int MinUsefulRows = 6;

    /// <summary>Intervals tried in order, the first that fits winning.</summary>
    private static readonly TimeSpan[] Intervals =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(4),
    ];

    public static Chart Build(IReadOnlyCollection<EventViewershipBucket> buckets, int? sessionId, int maxRows = MaxRows)
    {
        var relevant = buckets.Where(b => b.SessionId == sessionId).ToList();
        if (relevant.Count == 0)
        {
            return new Chart([], ViewershipAggregator.BucketLength, 0);
        }

        var starts = relevant.Select(b => b.BucketStartUtc).Distinct().OrderBy(t => t).ToList();
        var interval = ChooseInterval(starts.Count, maxRows);

        var byType = ViewerClientTypes.Reported
            .Select(type => relevant.Where(b => b.ClientType == type).ToDictionary(b => b.BucketStartUtc))
            .ToList();
        var all = relevant.Where(b => b.ClientType == ViewerClientTypes.All).ToDictionary(b => b.BucketStartUtc);

        var perRow = (int)(interval.Ticks / ViewershipAggregator.BucketLength.Ticks);
        var rows = new List<ChartRow>();

        for (var i = 0; i < starts.Count; i += perRow)
        {
            var group = starts.Skip(i).Take(perRow).ToList();

            // The mean of the children, because each covers the same span. The maximum is the largest
            // of theirs - a maximum is the largest value anywhere inside, at any width.
            var averages = byType
                .Select(lookup => group.Where(lookup.ContainsKey).Select(t => lookup[t].AvgConcurrent).DefaultIfEmpty(0).Average())
                .ToArray();
            var max = group.Where(all.ContainsKey).Select(t => all[t].MaxConcurrent).DefaultIfEmpty(0).Max();

            rows.Add(new ChartRow(group[0], averages, max));
        }

        var scale = rows.Count == 0 ? 0 : rows.Max(r => r.Total);
        return new Chart(rows, interval, scale);
    }

    /// <summary>
    /// The narrowest interval from <see cref="Intervals"/> that fits the buckets into
    /// <paramref name="maxRows"/>, widening past the list when none of them do.
    /// </summary>
    /// <remarks>
    /// Falling back to the widest listed interval is not enough. At the maximum reportable window a
    /// series is 384 buckets, which is 24 rows even at four hours - so any budget below 24 would be
    /// silently ignored, and the report-wide budget that keeps the email under Gmail's clipping
    /// threshold would not actually bound anything. The computed fallback is what makes the budget
    /// real.
    /// </remarks>
    private static TimeSpan ChooseInterval(int bucketCount, int maxRows)
    {
        if (maxRows <= 0)
        {
            maxRows = 1;
        }

        foreach (var interval in Intervals)
        {
            var perRow = (int)(interval.Ticks / ViewershipAggregator.BucketLength.Ticks);
            if ((bucketCount + perRow - 1) / perRow <= maxRows)
            {
                return interval;
            }
        }

        // However many buckets have to share a row for the count to fit, rounded to a whole number
        // of buckets so every row still covers the same span.
        var needed = (bucketCount + maxRows - 1) / maxRows;
        return ViewershipAggregator.BucketLength * needed;
    }

    /// <summary>How the chart's interval is described above it.</summary>
    public static string Describe(TimeSpan interval) => interval.TotalMinutes switch
    {
        < 60 => $"{interval.TotalMinutes:0}-minute intervals",
        60 => "1-hour intervals",
        _ => $"{interval.TotalHours:0}-hour intervals",
    };
}
