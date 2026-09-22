namespace RedMist.Backend.Shared.Utilities;

/// <summary>One viewer session reduced to what the sweep needs.</summary>
public readonly record struct ViewerInterval(DateTime StartUtc, DateTime EndUtc, string ClientType);

/// <summary>Concurrency over one bucket.</summary>
/// <param name="StartUtc">Start of the bucket.</param>
/// <param name="EndUtc">
/// End of the bucket: one bucket length after the start, except for a last bucket cut short by the end
/// of the window, which ends where the window does.
/// </param>
/// <param name="Min">Lowest concurrency held for a non-zero length of time.</param>
/// <param name="Max">Highest concurrency held for a non-zero length of time.</param>
/// <param name="ConnectedSeconds">
/// Total connected seconds inside the bucket, unrounded. Kept alongside the rounded
/// <see cref="ViewerSeconds"/> because a short bucket's average cannot be taken from a whole number
/// of seconds: one viewer across the first 0.6s of a bucket is 0.6 connected seconds, which rounds
/// to 1 and would read as 1.7 viewers.
/// </param>
public readonly record struct BucketConcurrency(DateTime StartUtc, DateTime EndUtc, int Min, int Max,
    double ConnectedSeconds)
{
    /// <summary>Total connected seconds inside the bucket, rounded once.</summary>
    public long ViewerSeconds => (long)Math.Round(ConnectedSeconds);

    /// <summary>Time-weighted mean concurrency over the full bucket length.</summary>
    /// <remarks>
    /// Divides by the nominal length whatever the bucket actually covered, which is what the
    /// post-event report stores. For a bucket the window cut short, see <see cref="AverageOverSpan"/>.
    /// </remarks>
    public double Average(TimeSpan bucketLength) => ViewerSeconds / bucketLength.TotalSeconds;

    /// <summary>
    /// Time-weighted mean concurrency over the stretch this bucket actually spans.
    /// </summary>
    /// <remarks>
    /// The same as <see cref="Average"/> for every full bucket. It differs only for a last bucket cut
    /// short by the window end - the minute a live chart is still in, which has only been running for
    /// the seconds since it began. Dividing that by the full length would draw every live line
    /// sagging toward zero at "now", by exactly the fraction of the minute still to come. A bucket of
    /// no length has no mean, and reads as zero.
    /// </remarks>
    public double AverageOverSpan()
    {
        var span = (EndUtc - StartUtc).TotalSeconds;
        return span > 0 ? ConnectedSeconds / span : 0;
    }
}

/// <summary>
/// Turns a set of overlapping viewer sessions into per-bucket concurrency.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the post-event report and the organizer's live viewership view, at their own bucket
/// lengths, so that the numbers an organizer watches during the event are the same arithmetic as the
/// ones emailed after it. A second copy would eventually disagree with the first.
/// </para>
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
    /// the result has no gaps in its axis. A window that is not a whole number of buckets ends in a
    /// short one, whose minimum and maximum are taken over only the part inside the window.
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
            // Accumulated as a double and rounded once, by ViewerSeconds. Rounding each sub-interval
            // would drift, and the per-type series summing exactly to the all-types series is what
            // makes a stacked bar of averages honest.
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

            buckets.Add(new BucketConcurrency(bucketStart, bucketEnd, min == int.MaxValue ? 0 : min, max,
                viewerSeconds));
        }

        return buckets;
    }

    /// <summary>
    /// The highest concurrency in a series, and the start of the first bucket that reached it.
    /// </summary>
    /// <remarks>
    /// The first bucket, so the reported time is when the crowd arrived rather than the last moment
    /// it happened to still be there. No time is given for a peak of zero: nobody watching has no
    /// moment worth pointing at, and a timestamp there would send someone looking for a crowd that
    /// never came.
    /// </remarks>
    /// <param name="buckets">Each bucket's start and maximum, in time order.</param>
    public static (int Max, DateTime? AtUtc) Peak(IEnumerable<(DateTime StartUtc, int Max)> buckets)
    {
        var max = 0;
        DateTime? at = null;
        foreach (var (startUtc, bucketMax) in buckets)
        {
            // Strictly greater, so a later bucket that only equals the peak does not move it.
            if (bucketMax > max)
            {
                max = bucketMax;
                at = startUtc;
            }
        }

        return (max, at);
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
