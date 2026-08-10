using RedMist.EventProcessor.EventStatus;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// The metrics wrapper is what the pipeline reports its own health from, and the health score is
/// read off the active-message gauge and the output/input ratio. A block that leaks the gauge on
/// an exception, or that counts a filtered-out message as output, makes the pipeline look either
/// permanently backed up or permanently healthy.
///
/// Prometheus collectors are process-global and keyed by label, so every test uses its own block
/// name and never sees another test's counts.
/// </summary>
[TestClass]
public class PipelineMetricsTests
{
    private static PipelineMetrics NewMetrics() => new($"test_block_{Guid.NewGuid():N}");

    private static PatchUpdates Patches(int sessionPatches, int carPatches) => new(
        [.. Enumerable.Range(0, sessionPatches).Select(_ => new SessionStatePatch())],
        [.. Enumerable.Range(0, carPatches).Select(i => new CarPositionPatch { Number = i.ToString() })]);

    /// <summary>
    /// A transform block that produced no patches filtered the message out. Counting it as output
    /// would put the filter ratio at 1 for a block that is passing nothing on.
    /// </summary>
    [TestMethod]
    public async Task TrackAsync_EmptyPatchUpdates_IsNotCountedAsOutput()
    {
        var metrics = NewMetrics();

        await metrics.TrackAsync(() => Task.FromResult(Patches(0, 0)));

        var m = metrics.GetCurrentMetrics();
        Assert.AreEqual(1, m.MessagesProcessed);
        Assert.AreEqual(0, m.MessagesOutput);
        Assert.AreEqual(0, m.FilterRatio);
    }

    [TestMethod]
    public async Task TrackAsync_PatchUpdatesCarryingAnyPatch_IsCountedAsOutput()
    {
        var metrics = NewMetrics();

        await metrics.TrackAsync(() => Task.FromResult(Patches(1, 0)));
        await metrics.TrackAsync(() => Task.FromResult(Patches(0, 1)));

        var m = metrics.GetCurrentMetrics();
        Assert.AreEqual(2, m.MessagesProcessed);
        Assert.AreEqual(2, m.MessagesOutput, "Either kind of patch is output.");
        Assert.AreEqual(1, m.FilterRatio);
    }

    /// <summary>
    /// A null result is the pipeline's "nothing to pass on" for every non-PatchUpdates block.
    /// </summary>
    [TestMethod]
    public async Task TrackAsync_NullResult_IsNotCountedAsOutput()
    {
        var metrics = NewMetrics();

        await metrics.TrackAsync(() => Task.FromResult<string?>(null));
        await metrics.TrackAsync(() => Task.FromResult<string?>("something"));

        var m = metrics.GetCurrentMetrics();
        Assert.AreEqual(2, m.MessagesProcessed);
        Assert.AreEqual(1, m.MessagesOutput);
        Assert.AreEqual(0.5, m.FilterRatio);
    }

    [TestMethod]
    public void Track_NullResult_IsNotCountedAsOutput()
    {
        var metrics = NewMetrics();

        metrics.Track(() => (string?)null);

        var m = metrics.GetCurrentMetrics();
        Assert.AreEqual(1, m.MessagesProcessed);
        Assert.AreEqual(0, m.MessagesOutput);
    }

    /// <summary>
    /// BUG (pinned, not fixed): the synchronous <c>Track&lt;T&gt;</c> has none of the empty-PatchUpdates
    /// filtering its async sibling has - it is a bare <c>if (result != null)</c> - so a block that produced
    /// no patches is still counted as output.
    ///
    /// That overload is what the multiloop and position-metadata blocks use
    /// (<c>SessionStateProcessingPipeline.ProcessMultiloop</c> and <c>ProcessPositionEnrichment</c>), and
    /// <c>MultiloopProcessor.Process</c> always hands back a non-null PatchUpdates. Both blocks therefore
    /// report a filter ratio pinned at 1.0 for the life of the process and can never be seen filtering,
    /// which is precisely the "permanently healthy" reading this file's header warns about.
    ///
    /// This asserts the current, wrong behavior so the difference between the two overloads is visible
    /// and a fix to <c>PipelineMetrics.Track&lt;T&gt;</c> shows up here rather than passing silently.
    /// </summary>
    [TestMethod]
    public void Track_EmptyPatchUpdates_IsStillCountedAsOutput()
    {
        var metrics = NewMetrics();

        metrics.Track(() => Patches(0, 0));

        var m = metrics.GetCurrentMetrics();
        Assert.AreEqual(1, m.MessagesProcessed);
        Assert.AreEqual(1, m.MessagesOutput,
            "If this now reads 0 the sync overload has gained the filter; update the async-sibling comparison below too.");
        Assert.AreEqual(1, m.FilterRatio, "A block that filtered everything out reports a perfect filter ratio.");
    }

    /// <summary>
    /// The two overloads disagree on the same input. Pinned as a pair so the asymmetry cannot be read as
    /// an accident of either test on its own.
    /// </summary>
    [TestMethod]
    public async Task TrackOverloads_GivenTheSameEmptyPatchUpdates_DisagreeOnWhetherItIsOutput()
    {
        var syncMetrics = NewMetrics();
        var asyncMetrics = NewMetrics();

        syncMetrics.Track(() => Patches(0, 0));
        await asyncMetrics.TrackAsync(() => Task.FromResult(Patches(0, 0)));

        // BUG (pinned, not fixed): these should be equal.
        Assert.AreEqual(1, syncMetrics.GetCurrentMetrics().MessagesOutput);
        Assert.AreEqual(0, asyncMetrics.GetCurrentMetrics().MessagesOutput);
    }

    /// <summary>
    /// The action overloads have no result to filter on, so every completed call is output.
    /// </summary>
    [TestMethod]
    public async Task TrackAction_Completing_CountsOutput()
    {
        var syncMetrics = NewMetrics();
        var asyncMetrics = NewMetrics();
        var ran = 0;

        syncMetrics.Track(() => ran++);
        await asyncMetrics.TrackAsync(() => { ran++; return Task.CompletedTask; });

        Assert.AreEqual(2, ran);
        Assert.AreEqual((1, 1), (syncMetrics.GetCurrentMetrics().MessagesProcessed, syncMetrics.GetCurrentMetrics().MessagesOutput));
        Assert.AreEqual((1, 1), (asyncMetrics.GetCurrentMetrics().MessagesProcessed, asyncMetrics.GetCurrentMetrics().MessagesOutput));
    }

    /// <summary>
    /// The active-message gauge is what the pipeline's health score reads as "backed up". A block
    /// that throws must still hand its slot back, or the block reads as permanently congested for
    /// the rest of the process's life. The exception itself has to reach the caller: the pipeline
    /// is what decides a failed message is not an error worth stopping for.
    /// </summary>
    [TestMethod]
    public async Task AllOverloads_WhenOperationThrows_ReleaseActiveCountAndRethrow()
    {
        var syncFunc = NewMetrics();
        var asyncFunc = NewMetrics();
        var syncAction = NewMetrics();
        var asyncAction = NewMetrics();

        Assert.ThrowsExactly<InvalidOperationException>(() => syncFunc.Track<string>(() => throw new InvalidOperationException()));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => asyncFunc.TrackAsync<string>(() => throw new InvalidOperationException()));
        Assert.ThrowsExactly<InvalidOperationException>(() => syncAction.Track(() => throw new InvalidOperationException()));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => asyncAction.TrackAsync(() => throw new InvalidOperationException()));

        foreach (var (name, metrics) in new[]
        {
            ("Track<T>", syncFunc), ("TrackAsync<T>", asyncFunc), ("Track(Action)", syncAction), ("TrackAsync(Func<Task>)", asyncAction),
        })
        {
            var m = metrics.GetCurrentMetrics();
            Assert.AreEqual(0, m.ActiveMessages, $"{name} left the message counted as still in flight.");
            Assert.AreEqual(1, m.MessagesProcessed, $"{name} did not count the failed message as processed.");
            Assert.AreEqual(0, m.MessagesOutput, $"{name} counted a failed message as output.");
        }
    }

    /// <summary>
    /// With nothing processed the filter ratio must not be a division by zero.
    /// </summary>
    [TestMethod]
    public void GetCurrentMetrics_BeforeAnyMessages_ReportsZeroes()
    {
        var m = NewMetrics().GetCurrentMetrics();

        Assert.AreEqual(0, m.MessagesProcessed);
        Assert.AreEqual(0, m.MessagesOutput);
        Assert.AreEqual(0, m.ActiveMessages);
        Assert.AreEqual(0, m.AvgProcessingTime);
        Assert.AreEqual(0, m.FilterRatio);
    }

    /// <summary>
    /// The gauge has to be raised for the duration of the call, not merely balanced by the end of
    /// it - it is the only signal that a block is holding messages.
    /// </summary>
    [TestMethod]
    public async Task TrackAsync_WhileOperationRuns_CountsTheMessageAsActive()
    {
        var metrics = NewMetrics();
        var release = new TaskCompletionSource();
        double activeDuring = -1;

        var tracked = metrics.TrackAsync(async () =>
        {
            activeDuring = metrics.GetCurrentMetrics().ActiveMessages;
            await release.Task;
            return "done";
        });

        release.SetResult();
        await tracked;

        Assert.AreEqual(1, activeDuring);
        Assert.AreEqual(0, metrics.GetCurrentMetrics().ActiveMessages);
    }
}
