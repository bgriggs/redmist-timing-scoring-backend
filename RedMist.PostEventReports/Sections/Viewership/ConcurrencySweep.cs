namespace RedMist.PostEventReports.Sections.Viewership;

/// <summary>One viewer session reduced to what the sweep needs.</summary>
public readonly record struct ViewerInterval(DateTime StartUtc, DateTime EndUtc, string ClientType);

/// <summary>Concurrency over one bucket.</summary>
/// <param name="StartUtc">Start of the bucket.</param>
/// <param name="Min">Lowest concurrency held for a non-zero length of time.</param>
/// <param name="Max">Highest concurrency held for a non-zero length of time.</param>
/// <param name="ViewerSeconds">Total connected seconds inside the bucket.</param>
public readonly record struct BucketConcurrency(DateTime StartUtc, int Min, int Max, long ViewerSeconds)
{
    /// <summary>Time-weighted mean concurrency over the full bucket length.</summary>
    public double Average(TimeSpan bucketLength) => ViewerSeconds / bucketLength.TotalSeconds;
}

/// <summary>
/// Turns a set of overlapping viewer sessions into per-bucket concurrency.
/// </summary>
/// <remarks>
/// <para>
/// One pass over the sorted endpoints rather than one pass per bucket, with the bucket boundaries
/// injected as zero-delta markers so a single sweep serves every bucket.
/// </para>
/// <para>
/// Three properties this has to get right, each of which is a wrong number rather than a crash if it
/// does not:
/// </para>
/// <list type="number">
/// <item>
/// Sessions are half-open intervals and a departure is applied before an arrival at the same instant,
/// so a clean handoff - one connection ending exactly as another begins - reads as one viewer rather
/// than two. Getting this backwards inflates every peak on a client that reconnects, which given how
/// often phones reconnect is most of them.
/// </item>
/// <item>
/// All the deltas at one instant are applied together, and only sub-intervals of positive length are
/// sampled. Without that, a simultaneous arrival and departure produces a dip to one fewer that
/// existed for zero time, and a minimum defined as "the lowest the counter ever held" would report
/// it.
/// </item>
/// <item>
/// The all-types series is swept in its own right, never summed from the per-type series. Maxima do
/// not add: iOS peaking at ten past and web peaking at twenty past would sum to a number that never
/// happened.
/// </item>
/// </list>
/// </remarks>
public static class ConcurrencySweep
{
    /// <summary>
    /// Concurrency per bucket across <paramref name="windowStartUtc"/> to <paramref name="windowEndUtc"/>.
    /// </summary>
    /// <remarks>
    /// Every bucket in the window is returned, including ones nothing overlaps, so a chart drawn from
    /// the result has no gaps in its axis.
    /// </remarks>
    public static List<BucketConcurrency> Run(IEnumerable<ViewerInterval> intervals,
        DateTime windowStartUtc, DateTime windowEndUtc, TimeSpan bucketLength)
    {
        var buckets = new List<BucketConcurrency>();
        if (windowEndUtc <= windowStartUtc || bucketLength <= TimeSpan.Zero)
        {
            return buckets;
        }

        // +1 on arrival, -1 on departure. Clipped to the window, and zero-length intervals contribute
        // nothing: they raise no peak and hold for no time.
        var deltas = new List<(DateTime At, int Delta)>();
        foreach (var interval in intervals)
        {
            var start = interval.StartUtc < windowStartUtc ? windowStartUtc : interval.StartUtc;
            var end = interval.EndUtc > windowEndUtc ? windowEndUtc : interval.EndUtc;
            if (end <= start)
            {
                continue;
            }

            deltas.Add((start, 1));
            deltas.Add((end, -1));
        }

        // Departures before arrivals at the same instant, so a handoff never counts as two.
        deltas.Sort(static (a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Delta.CompareTo(b.Delta));

        var index = 0;
        var concurrency = 0;

        for (var bucketStart = windowStartUtc; bucketStart < windowEndUtc; bucketStart += bucketLength)
        {
            var bucketEnd = bucketStart + bucketLength;
            if (bucketEnd > windowEndUtc)
            {
                bucketEnd = windowEndUtc;
            }

            var min = int.MaxValue;
            var max = 0;
            // Accumulated as a double and rounded once. Rounding each sub-interval would drift, and
            // the per-type series summing exactly to the all-types series is what makes a stacked
            // bar of averages honest.
            var viewerSeconds = 0d;
            var cursor = bucketStart;

            while (index < deltas.Count && deltas[index].At < bucketEnd)
            {
                var at = deltas[index].At;

                // The stretch from the cursor to this instant held the current concurrency. Sampled
                // only when it has positive length, so a simultaneous arrival and departure cannot
                // register a dip that lasted no time.
                if (at > cursor)
                {
                    Observe(concurrency, at - cursor, ref min, ref max, ref viewerSeconds);
                    cursor = at;
                }

                // Every delta at this instant, together.
                while (index < deltas.Count && deltas[index].At == at)
                {
                    concurrency += deltas[index].Delta;
                    index++;
                }
            }

            if (bucketEnd > cursor)
            {
                Observe(concurrency, bucketEnd - cursor, ref min, ref max, ref viewerSeconds);
            }

            buckets.Add(new BucketConcurrency(bucketStart, min == int.MaxValue ? 0 : min, max,
                (long)Math.Round(viewerSeconds)));
        }

        return buckets;
    }

    private static void Observe(int concurrency, TimeSpan held, ref int min, ref int max, ref double viewerSeconds)
    {
        if (concurrency < min)
        {
            min = concurrency;
        }
        if (concurrency > max)
        {
            max = concurrency;
        }
        viewerSeconds += concurrency * held.TotalSeconds;
    }
}
