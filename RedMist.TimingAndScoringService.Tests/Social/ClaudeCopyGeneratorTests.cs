using BigMission.TestHelpers.Testing;
using Moq;
using RedMist.Social.Generation;
using RedMist.TimingAndScoringService.Tests.ExternalDataCollection;
using System.Net;
using System.Text;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.Social;

/// <summary>
/// Covers the HTTP contract with the Messages API: what gets sent, what a reply is read as, and which
/// failures are worth another attempt. The parsing cases matter most -- misreading a reply produces a
/// draft that looks fine and is wrong.
/// </summary>
[TestClass]
public class ClaudeCopyGeneratorTests
{
    private const string ApiKey = "sk-ant-not-a-real-key";

    private static readonly CopyRequest Request = new(
        SystemPrompt: "Write a short post.",
        Messages: [new(CopyRole.User, "Here are the results.")],
        Model: "claude-sonnet-5",
        MaxTokens: 1024);

    /// <summary>
    /// A clock whose timers fire straight away, so the retry backoff costs no wall-clock time.
    /// </summary>
    /// <remarks>
    /// FakeTimeProvider cannot serve here. Its timers only fire when something advances the clock, and
    /// nothing reads the clock while the generator is sitting in its backoff, so the delay would never
    /// complete and the test would hang rather than fail. Real time would work but adds roughly
    /// fourteen seconds to the suite for the give-up case alone.
    /// </remarks>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ImmediateTimer(callback, state);

        private sealed class ImmediateTimer : ITimer
        {
            public ImmediateTimer(TimerCallback callback, object? state)
            {
                // Queued rather than run inline, so the callback cannot re-enter the caller before it
                // has finished wiring up the continuation it is about to complete.
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static ClaudeCopyGenerator Build(StubHttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new ClaudeCopyGenerator(factory.Object, ApiKey, new DebugLoggerFactory(), new ImmediateTimeProvider());
    }

    private static string Reply(string text, string stopReason = "end_turn", string model = "claude-sonnet-5-20260514") =>
        JsonSerializer.Serialize(new
        {
            id = "msg_01",
            type = "message",
            role = "assistant",
            model,
            content = new[] { new { type = "text", text } },
            stop_reason = stopReason,
        });

    [TestMethod]
    public async Task ASuccessfulReply_IsReadAsCopy()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Reply("  Bad Decision Racing took the win.  "));
        var generator = Build(handler);

        var copy = await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.AreEqual("Bad Decision Racing took the win.", copy.Text, "The reply should be trimmed");
        Assert.AreEqual("claude-sonnet-5-20260514", copy.Model, "The serving model, not the requested one");
        Assert.IsFalse(copy.WasTruncated);
    }

    /// <summary>
    /// A reply can arrive as several text blocks. Taking only the first would publish a fragment that
    /// reads as complete, which is exactly the kind of failure nothing downstream would catch.
    /// </summary>
    [TestMethod]
    public async Task AReplySplitAcrossBlocks_IsJoinedRatherThanTruncated()
    {
        var body = JsonSerializer.Serialize(new
        {
            model = "claude-sonnet-5",
            content = new[]
            {
                new { type = "text", text = "Bad Decision Racing took GP1. " },
                new { type = "text", text = "Slow And Steady followed a lap down." },
            },
            stop_reason = "end_turn",
        });

        var generator = Build(StubHttpMessageHandler.Json(HttpStatusCode.OK, body));
        var copy = await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.AreEqual("Bad Decision Racing took GP1. Slow And Steady followed a lap down.", copy.Text);
    }

    [TestMethod]
    public async Task AReplyCutOffAtTheTokenCeiling_IsReportedAsTruncated()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Reply("Bad Decision Racing took GP1 and then the", "max_tokens"));
        var generator = Build(handler);

        var copy = await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.IsTrue(copy.WasTruncated);
    }

    [TestMethod]
    public async Task TheRequest_CarriesTheApiKeyAndVersionHeaders()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Reply("ok"));
        var generator = Build(handler);

        await generator.GenerateAsync(Request, CancellationToken.None);

        var sent = handler.Requests.Single();
        Assert.AreEqual(ApiKey, sent.Headers["x-api-key"]);
        Assert.AreEqual("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.AreEqual("application/json", sent.ContentType);
    }

    /// <summary>
    /// The wire format is snake_case while the C# is not, so a rename that broke the mapping would
    /// otherwise surface as a confusing 400 from the API rather than a failing test.
    /// </summary>
    [TestMethod]
    public async Task TheRequestBody_UsesTheWireFieldNames()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Reply("ok"));
        var generator = Build(handler);

        await generator.GenerateAsync(
            Request with { Messages = [new(CopyRole.User, "first"), new(CopyRole.Assistant, "second")] },
            CancellationToken.None);

        var body = Encoding.UTF8.GetString(handler.Requests.Single().Body);
        using var parsed = JsonDocument.Parse(body);
        var root = parsed.RootElement;

        Assert.AreEqual("claude-sonnet-5", root.GetProperty("model").GetString());
        Assert.AreEqual(1024, root.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual("Write a short post.", root.GetProperty("system").GetString());

        var messages = root.GetProperty("messages");
        Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("assistant", messages[1].GetProperty("role").GetString());
        Assert.AreEqual("second", messages[1].GetProperty("content").GetString());
    }

    /// <summary>
    /// Rate limiting is the expected failure when several events are drafted back to back, and giving
    /// up on the first one would cost the whole run's remaining posts.
    /// </summary>
    [TestMethod]
    public async Task ARateLimitedRequest_IsRetried()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Reply("Bad Decision Racing took GP1."), Encoding.UTF8, "application/json"),
                };
        });

        var generator = Build(handler);
        var copy = await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.AreEqual(2, calls);
        Assert.AreEqual("Bad Decision Racing took GP1.", copy.Text);
    }

    [TestMethod]
    public async Task AServerError_IsRetried()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            return calls < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("overloaded") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Reply("ok"), Encoding.UTF8, "application/json"),
                };
        });

        var generator = Build(handler);
        await generator.GenerateAsync(Request, CancellationToken.None);

        Assert.AreEqual(3, calls);
    }

    /// <summary>
    /// A rejected request will be rejected identically next time. Retrying a bad model name or a
    /// revoked key just spends the job's time before failing anyway.
    /// </summary>
    [TestMethod]
    public async Task ARejectedRequest_IsNotRetried()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"model not found"}}""");
        var generator = Build(handler);

        var ex = await Assert.ThrowsExactlyAsync<CopyGenerationException>(
            () => generator.GenerateAsync(Request, CancellationToken.None));

        Assert.AreEqual(1, handler.Requests.Count);
        StringAssert.Contains(ex.Message, "model not found");
    }

    /// <summary>
    /// The message ends up in a log and, for a failed post, in a database column a reviewer reads. A
    /// key reaching either would be a credential leak with a long tail.
    /// </summary>
    [TestMethod]
    public async Task AFailureMessage_DoesNotContainTheApiKey()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key"}}""");
        var generator = Build(handler);

        var ex = await Assert.ThrowsExactlyAsync<CopyGenerationException>(
            () => generator.GenerateAsync(Request, CancellationToken.None));

        StringAssert.DoesNotMatch(ex.ToString(), new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(ApiKey)));
    }

    [TestMethod]
    public async Task PersistentTransportFailure_GivesUpRatherThanRetryingForever()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var generator = Build(handler);

        await Assert.ThrowsExactlyAsync<CopyGenerationException>(
            () => generator.GenerateAsync(Request, CancellationToken.None));

        Assert.AreEqual(4, handler.Requests.Count, "Four attempts, then the event is left for the next run");
    }

    /// <summary>
    /// HttpClient surfaces its own timeout as a TaskCanceledException, not an HttpRequestException.
    /// Missing that would leave the most retryable failure there is unretried, and would also make it
    /// indistinguishable from host shutdown to every caller above -- aborting the whole run.
    /// </summary>
    [TestMethod]
    public async Task AClientTimeout_IsRetriedRatherThanReadAsCancellation()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            if (calls == 1)
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Reply("Bad Decision Racing took GP1."), Encoding.UTF8, "application/json"),
            };
        });

        var copy = await Build(handler).GenerateAsync(Request, CancellationToken.None);

        Assert.AreEqual(2, calls);
        Assert.AreEqual("Bad Decision Racing took GP1.", copy.Text);
    }

    /// <summary>
    /// After the attempts are spent, a timeout must surface as a generation failure. Letting an
    /// OperationCanceledException escape would read as host shutdown to the job and abort the run.
    /// </summary>
    [TestMethod]
    public async Task APersistentTimeout_SurfacesAsAGenerationFailureNotACancellation()
    {
        var handler = StubHttpMessageHandler.Throws(
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));

        await Assert.ThrowsExactlyAsync<CopyGenerationException>(
            () => Build(handler).GenerateAsync(Request, CancellationToken.None));
    }

    /// <summary>
    /// Genuine shutdown must still cancel rather than being retried as though it were a timeout, and
    /// must not spend a request on the way out.
    /// </summary>
    [TestMethod]
    public async Task RealCancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, Reply("ok"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Build(handler).GenerateAsync(Request, cts.Token));

        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AMalformedBody_FailsLoudlyRatherThanProducingEmptyCopy()
    {
        var generator = Build(StubHttpMessageHandler.Json(HttpStatusCode.OK, "not json at all"));

        await Assert.ThrowsExactlyAsync<CopyGenerationException>(
            () => generator.GenerateAsync(Request, CancellationToken.None));
    }
}
