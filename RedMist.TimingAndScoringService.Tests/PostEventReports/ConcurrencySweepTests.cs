using RedMist.PostEventReports.Sections.Viewership;

namespace RedMist.TimingAndScoringService.Tests.PostEventReports;

/// <summary>
/// Covers the concurrency rules every viewership number rests on.
/// </summary>
/// <remarks>
/// Pure arithmetic with no fixture, because these are the calculations most easily got subtly wrong:
/// every mistake here produces a plausible number rather than a failure, and the number goes to an
/// organizer as fact.
/// </remarks>
[TestClass]
public class ConcurrencySweepTests
{
    private static readonly DateTime Origin = new(2026, 9, 19, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(15);

    private static ViewerInterval Watch(int startMinute, int endMinute, string clientType = "Web")
        => new(Origin.AddMinutes(startMinute), Origin.AddMinutes(endMinute), clientType);

    private static List<BucketConcurrency> Sweep(IEnumerable<ViewerInterval> intervals, int windowMinutes = 15)
        => ConcurrencySweep.Run(intervals, Origin, Origin.AddMinutes(windowMinutes), Bucket);

    [TestMethod]
    public void SessionSpanningTheWholeBucket_IsOneThroughout()
    {
        var bucket = Sweep([Watch(0, 15)]).Single();

        Assert.AreEqual(1, bucket.Min);
        Assert.AreEqual(1, bucket.Max);
        Assert.AreEqual(1d, bucket.Average(Bucket), 0.0001);
    }

    /// <summary>
    /// The minimum covers the whole bucket, including the stretch before anyone arrived. Measuring it
    /// over only the covered portion would report "at least 1 watching" for a bucket where nobody
    /// watched for half of it, which flatters us and is not true.
    /// </summary>
    [TestMethod]
    public void SessionCoveringHalfTheBucket_HasAMinimumOfZero()
    {
        var bucket = Sweep([Watch(0, 8)]).Single();

        Assert.AreEqual(0, bucket.Min);
        Assert.AreEqual(1, bucket.Max);
    }

    [TestMethod]
    public void EmptyBucket_IsAllZeroes()
    {
        var bucket = Sweep([]).Single();

        Assert.AreEqual(0, bucket.Min);
        Assert.AreEqual(0, bucket.Max);
        Assert.AreEqual(0d, bucket.Average(Bucket), 0.0001);
    }

    /// <summary>
    /// A clean handoff - one connection ending exactly as another begins. Sessions are half-open, so
    /// this is one viewer, not two. Getting it backwards would inflate every peak on a client that
    /// reconnects, and phones reconnect constantly.
    /// </summary>
    [TestMethod]
    public void OneSessionEndingExactlyAsAnotherBegins_PeaksAtOne()
    {
        var bucket = Sweep([Watch(0, 5), Watch(5, 15)]).Single();

        Assert.AreEqual(1, bucket.Max);
    }

    /// <summary>
    /// The same handoff seen from the other side: the counter momentarily reads one fewer, but for no
    /// time at all. A minimum defined as "the lowest the counter ever held" would report a dip that
    /// never existed.
    /// </summary>
    [TestMethod]
    public void BackToBackSessionsAcrossAFullBucket_NeverDipToZero()
    {
        var bucket = Sweep([Watch(0, 5), Watch(5, 15)]).Single();

        Assert.AreEqual(1, bucket.Min);
    }

    [TestMethod]
    public void TwoOverlappingSessions_PeakAtTwo()
    {
        var bucket = Sweep([Watch(0, 15), Watch(0, 15)]).Single();

        Assert.AreEqual(2, bucket.Min);
        Assert.AreEqual(2, bucket.Max);
        Assert.AreEqual(2d, bucket.Average(Bucket), 0.0001);
    }

    [TestMethod]
    public void SessionSpanningABucketBoundary_CountsInBoth()
    {
        var buckets = Sweep([Watch(10, 20)], windowMinutes: 45);

        Assert.HasCount(3, buckets);
        Assert.AreEqual(1, buckets[0].Max, "The session started inside the first bucket.");
        Assert.AreEqual(1, buckets[1].Max, "And ran on into the second.");
        Assert.AreEqual(0, buckets[2].Max, "But not into the third.");
    }

    [TestMethod]
    public void SessionEntirelyOutsideTheWindow_IsDropped()
    {
        var buckets = Sweep([new ViewerInterval(Origin.AddHours(-2), Origin.AddHours(-1), "Web")]);

        Assert.AreEqual(0, buckets.Single().Max);
    }

    [TestMethod]
    public void SessionStraddlingTheWindowEdge_IsClippedNotDropped()
    {
        var bucket = Sweep([new ViewerInterval(Origin.AddHours(-1), Origin.AddMinutes(15), "Web")]).Single();

        Assert.AreEqual(1, bucket.Min);
        Assert.AreEqual(1, bucket.Max);
    }

    [TestMethod]
    public void ZeroLengthSession_ContributesNothingAndRaisesNoPeak()
    {
        var bucket = Sweep([Watch(5, 5)]).Single();

        Assert.AreEqual(0, bucket.Max);
        Assert.AreEqual(0, bucket.ViewerSeconds);
    }

    /// <summary>
    /// The identity the whole chart rests on: mean concurrency over a bucket is the connected time
    /// inside it divided by the bucket length. It is what makes per-type averages sum exactly to the
    /// all-types average, and so what makes a stacked bar of averages honest.
    /// </summary>
    [TestMethod]
    public void TimeWeightedAverage_EqualsConnectedSecondsOverTheBucketLength()
    {
        var random = new Random(20260919);
        var intervals = Enumerable.Range(0, 40)
            .Select(_ =>
            {
                var start = random.Next(0, 55);
                return Watch(start, start + random.Next(1, 20));
            })
            .ToList();

        var buckets = ConcurrencySweep.Run(intervals, Origin, Origin.AddMinutes(90), Bucket);

        foreach (var bucket in buckets)
        {
            var expected = intervals.Sum(i => OverlapSeconds(i, bucket.StartUtc, bucket.StartUtc + Bucket));
            Assert.AreEqual(expected / Bucket.TotalSeconds, bucket.Average(Bucket), 0.01,
                $"Bucket at {bucket.StartUtc:HH:mm} disagrees with the overlap sum.");
        }
    }

    /// <summary>
    /// The one that stops someone "simplifying" the all-types series into a sum of the per-type ones.
    /// Averages would sum correctly; maxima do not, and the result would be a peak that never
    /// happened.
    /// </summary>
    [TestMethod]
    public void AllTypesPeak_IsLessThanTheSumOfThePerTypePeaks_WhenTheyPeakAtDifferentTimes()
    {
        List<ViewerInterval> intervals = [Watch(0, 5, "iOS"), Watch(0, 5, "iOS"), Watch(10, 15, "Web"), Watch(10, 15, "Web")];

        var all = Sweep(intervals).Single();
        var ios = Sweep(intervals.Where(i => i.ClientType == "iOS")).Single();
        var web = Sweep(intervals.Where(i => i.ClientType == "Web")).Single();

        Assert.AreEqual(2, ios.Max);
        Assert.AreEqual(2, web.Max);
        Assert.AreEqual(2, all.Max, "The two platforms never overlapped, so four was never watching at once.");
        Assert.IsTrue(all.Max < ios.Max + web.Max);
    }

    [TestMethod]
    public void PerTypeConnectedSeconds_SumToTheAllTypesConnectedSeconds()
    {
        List<ViewerInterval> intervals =
        [
            Watch(0, 12, "iOS"), Watch(3, 15, "Android"), Watch(0, 15, "Web"), Watch(8, 9, "API"),
        ];

        var all = Sweep(intervals).Single().ViewerSeconds;
        var perType = new[] { "iOS", "Android", "Web", "API" }
            .Sum(t => Sweep(intervals.Where(i => i.ClientType == t)).Single().ViewerSeconds);

        Assert.AreEqual(all, perType);
    }

    [TestMethod]
    public void LongWindow_ProducesOneBucketPerInterval()
    {
        var buckets = ConcurrencySweep.Run([Watch(0, 1)], Origin, Origin.AddHours(24), Bucket);

        Assert.HasCount(96, buckets);
    }

    /// <summary>A session ending exactly at the window end must not create an extra bucket.</summary>
    [TestMethod]
    public void SessionEndingExactlyAtTheWindowEnd_DoesNotCreateAnExtraBucket()
    {
        var buckets = ConcurrencySweep.Run([Watch(0, 60)], Origin, Origin.AddMinutes(60), Bucket);

        Assert.HasCount(4, buckets);
        Assert.AreEqual(1, buckets[^1].Max);
    }

    private static double OverlapSeconds(ViewerInterval interval, DateTime start, DateTime end)
    {
        var from = interval.StartUtc < start ? start : interval.StartUtc;
        var to = interval.EndUtc > end ? end : interval.EndUtc;
        return to <= from ? 0 : (to - from).TotalSeconds;
    }
}
