using RedMist.StatusLoadTest;
using System.Diagnostics;

namespace RedMist.EventProcessor.Tests.Tools;

/// <summary>
/// Covers the arithmetic the load test's report is built from. A load test that quietly reports the
/// wrong spread is worse than none, because the number is believed: these pin the cases where a
/// broadcast's observers are counted wrong, where a partly-delivered broadcast would be measured as
/// if it were whole, and where the percentile lands on the wrong sample.
/// </summary>
[TestClass]
public class StatusLoadTestReportingTests
{
    private static readonly long Second = Stopwatch.Frequency;

    private static Recorder RecorderAt(int clients, long rampCompleteTicks = 0)
    {
        var recorder = new Recorder(clients);
        recorder.RampCompleteTicks = rampCompleteTicks;
        return recorder;
    }

    [TestMethod]
    public void RecordBroadcast_TheSamePayloadSeenByEveryClient_IsOneBroadcastSpanningTheirArrivals()
    {
        var recorder = RecorderAt(3);
        recorder.RecordBroadcast(recorder[0], "session", "clock=1", 500);
        recorder.RecordBroadcast(recorder[1], "session", "clock=1", 900);
        recorder.RecordBroadcast(recorder[2], "session", "clock=1", 700);

        var broadcast = recorder.SettledBroadcasts(0).Single();
        Assert.AreEqual(3, broadcast.Receivers);
        Assert.AreEqual(500, broadcast.FirstTicks, "the earliest arrival anchors the spread");
        Assert.AreEqual(900, broadcast.LastTicks, "the latest arrival closes it");
    }

    [TestMethod]
    public void RecordBroadcast_DifferentPayloads_AreDifferentBroadcasts()
    {
        var recorder = RecorderAt(2);
        recorder.RecordBroadcast(recorder[0], "session", "clock=1", 100);
        recorder.RecordBroadcast(recorder[0], "session", "clock=2", 200);

        Assert.AreEqual(2, recorder.SettledBroadcasts(0).Count);
    }

    /// <summary>
    /// The same payload arriving twice at one client means content no longer identifies a single
    /// broadcast. Merging the two would report the gap between them as fan-out spread, which at a
    /// second or more would look like a serious delivery problem that is not there.
    /// </summary>
    [TestMethod]
    public void RecordBroadcast_APayloadRepeatedToOneClient_IsExcludedRatherThanMerged()
    {
        var recorder = RecorderAt(2);
        recorder.RecordBroadcast(recorder[0], "session", "clock=1", 100);
        recorder.RecordBroadcast(recorder[1], "session", "clock=1", 120);
        recorder.RecordBroadcast(recorder[0], "session", "clock=1", 5_000);

        Assert.AreEqual(0, recorder.SettledBroadcasts(0).Count);
        Assert.AreEqual(1, recorder.PoisonedBroadcasts);
    }

    /// <summary>
    /// The same content under two different hub methods is two different broadcasts; keying on
    /// content alone would fold them together.
    /// </summary>
    [TestMethod]
    public void RecordBroadcast_SameContentOnDifferentMethods_AreNotFoldedTogether()
    {
        var recorder = RecorderAt(1);
        recorder.RecordBroadcast(recorder[0], "session", "x", 100);
        recorder.RecordBroadcast(recorder[0], "cars", "x", 200);

        Assert.AreEqual(2, recorder.SettledBroadcasts(0).Count);
    }

    [TestMethod]
    public void SettledBroadcasts_ThoseAlreadyInFlightWhenTheRampEnded_AreNotMeasured()
    {
        var recorder = RecorderAt(2, rampCompleteTicks: 10 * Second);
        recorder.RecordBroadcast(recorder[0], "session", "during-ramp", 9 * Second);
        recorder.RecordBroadcast(recorder[0], "session", "in-grace", 11 * Second);
        recorder.RecordBroadcast(recorder[0], "session", "after", 13 * Second);

        var settled = recorder.SettledBroadcasts(graceTicks: 2 * Second);

        Assert.AreEqual(1, settled.Count, "only the broadcast that began after the ramp and its grace counts");
        Assert.AreEqual(13 * Second, settled[0].FirstTicks);
    }

    [TestMethod]
    public void Percentile_LandsOnTheSampleAtThatRank()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.AreEqual(5, Recorder.Percentile(sorted, 50));
        Assert.AreEqual(10, Recorder.Percentile(sorted, 95), "a p95 over ten samples is the tenth");
        Assert.AreEqual(10, Recorder.Percentile(sorted, 100));
        Assert.AreEqual(1, Recorder.Percentile(sorted, 1), "a rank below the first sample still resolves to it");
        Assert.AreEqual(1, Recorder.Percentile(sorted, 0), "a zero percentile clamps to the first sample");
    }

    [TestMethod]
    public void Percentile_WithNoSamples_IsNotANumberRatherThanZero()
    {
        // Zero would read as a perfect result on an empty run.
        Assert.IsTrue(double.IsNaN(Recorder.Percentile([], 50)));
    }

    [TestMethod]
    public void Distribution_IgnoresClientsThatNeverConnected()
    {
        // A client that failed to connect leaves its connect time NaN; counting it would drag the
        // distribution toward a value no client actually saw.
        var distribution = Distribution.Of([10, double.NaN, 20, 30]);

        Assert.AreEqual(3, distribution.Count);
        Assert.AreEqual(30, distribution.Max);
    }

    [TestMethod]
    public void Report_CountsABroadcastAsCompleteOnlyWhenEverySubscribedClientSawIt()
    {
        var recorder = RecorderAt(3);
        foreach (var client in recorder.Clients)
        {
            client.EverConnected = true;
            client.Subscribed = true;
        }

        recorder.RecordBroadcast(recorder[0], "session", "all", 5 * Second);
        recorder.RecordBroadcast(recorder[1], "session", "all", 5 * Second);
        recorder.RecordBroadcast(recorder[2], "session", "all", 5 * Second);
        recorder.RecordBroadcast(recorder[0], "session", "partial", 6 * Second);
        recorder.RecordBroadcast(recorder[1], "session", "partial", 6 * Second);

        var report = Report.Build(new Options { Clients = 3 }, recorder);

        Assert.AreEqual(2, report.SettledBroadcasts);
        Assert.AreEqual(1, report.BroadcastsDeliveredToEveryone);
        Assert.AreEqual(1, report.DeliveryShortfall.Max, "the partial broadcast missed one client");
    }

    /// <summary>
    /// A socket that opened but never subscribed receives nothing however healthy the hub is.
    /// Counting it as part of the audience would turn a few failed subscribes into a report of
    /// total fan-out collapse, hiding the real fault behind a far more alarming false one.
    /// </summary>
    [TestMethod]
    public void Report_AConnectedButUnsubscribedClient_IsNotPartOfTheAudience()
    {
        var recorder = RecorderAt(3);
        foreach (var client in recorder.Clients)
            client.EverConnected = true;
        recorder[0].Subscribed = true;
        recorder[1].Subscribed = true;
        // recorder[2] connected, then its subscribe failed.

        recorder.RecordBroadcast(recorder[0], "session", "all", 5 * Second);
        recorder.RecordBroadcast(recorder[1], "session", "all", 5 * Second);

        var report = Report.Build(new Options { Clients = 3 }, recorder);

        Assert.AreEqual(3, report.ConnectedClients);
        Assert.AreEqual(2, report.SubscribedClients);
        Assert.AreEqual(1, report.BroadcastsDeliveredToEveryone, "both subscribers saw it, so it reached everyone who could see it");
        Assert.AreEqual(0, report.DeliveryShortfall.Max, "the unsubscribed client is not a missed delivery");
    }

    [TestMethod]
    public void Report_FanOutSpread_IsMeasuredFromTheFirstArrivalToTheLast()
    {
        var recorder = RecorderAt(2);
        foreach (var client in recorder.Clients)
        {
            client.EverConnected = true;
            client.Subscribed = true;
        }

        recorder.RecordBroadcast(recorder[0], "session", "a", 5 * Second);
        recorder.RecordBroadcast(recorder[1], "session", "a", 5 * Second + Second / 4);

        var report = Report.Build(new Options { Clients = 2 }, recorder);

        Assert.AreEqual(250, report.FanOutSpreadMs.Max, 1);
    }

    [TestMethod]
    public void Options_HubUrl_IsDerivedFromTheApiUrlOverAWebsocketScheme()
    {
        var secure = Options.Parse(["--api-url", "https://api-test.redmist.racing/status", "--event-id", "81"]);
        Assert.AreEqual("wss://api-test.redmist.racing/status/event-status", secure.HubUrl);

        var plain = Options.Parse(["--api-url", "http://status-api:8080", "--event-id", "81"]);
        Assert.AreEqual("ws://status-api:8080/event-status", plain.HubUrl);
    }

    [TestMethod]
    public void Options_RejectsARunWithNothingToDo()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            Options.Parse(["--api-url", "http://x", "--event-id", "1", "--no-poll", "--no-websocket"]));
    }
}
