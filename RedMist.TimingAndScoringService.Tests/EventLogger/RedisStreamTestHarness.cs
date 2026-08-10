using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventLogger.Services;
using StackExchange.Redis;
using System.Reflection;

namespace RedMist.TimingAndScoringService.Tests.EventLogger;

/// <summary>
/// Shared plumbing for driving the EventLogger's Redis consumer loops.
/// </summary>
/// <remarks>
/// The consumers are <see cref="BackgroundService"/> loops with no natural exit: they poll the
/// stream forever. Tests drive them by canceling the loop's token from inside the mocked stream
/// read, which makes the subsequent <c>Task.Delay(..., token)</c> complete immediately rather
/// than waiting on the wall clock. Every exit therefore surfaces as an
/// <see cref="OperationCanceledException"/> out of the service's own catch/throttle path, which
/// <see cref="RunAsync"/> absorbs.
/// </remarks>
internal static class RedisStreamTestHarness
{
    /// <summary>Builds a stream entry with the given id and name/value fields.</summary>
    public static StreamEntry Entry(string id, params (string Name, string Value)[] fields)
        => new(id, [.. fields.Select(f => new NameValueEntry(f.Name, f.Value))]);

    /// <summary>
    /// Builds a <see cref="StreamGroupInfo"/>, whose constructor is internal to StackExchange.Redis.
    /// Only <see cref="StreamGroupInfo.Name"/> matters to the code under test.
    /// </summary>
    public static StreamGroupInfo GroupInfo(string name)
    {
        var ctor = typeof(StreamGroupInfo).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault();
        Assert.IsNotNull(ctor, "StreamGroupInfo no longer exposes a non-public constructor; this helper needs updating.");
        var args = ctor.GetParameters()
            .Select(p => p.Name == "name"
                ? name
                : p.ParameterType == typeof(string) ? "0-0" : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null))
            .ToArray();
        return (StreamGroupInfo)ctor.Invoke(args);
    }

    /// <summary>
    /// Serves <paramref name="batches"/> one per read. The read that follows the last batch cancels
    /// <paramref name="cts"/> and returns nothing, so the loop unwinds after the batches are fully
    /// processed with a live token.
    /// </summary>
    /// <remarks>
    /// Cancellation only happens once every batch has been served, so the batches are processed with
    /// a live token and their database writes go through. Do not use this when processing a batch is
    /// expected to throw before it reaches the database: the loop's catch then waits out a real 10
    /// second throttle delay. Use <see cref="SetupReadsCancelingFirst"/> for that.
    /// </remarks>
    public static void SetupReads(Mock<IDatabase> cache, CancellationTokenSource cts, params StreamEntry[][] batches)
    {
        var call = 0;
        StreamRead(cache).ReturnsAsync(() =>
        {
            if (call < batches.Length)
                return batches[call++];
            cts.Cancel();
            return [];
        });
    }

    /// <summary>
    /// Cancels <paramref name="cts"/> before handing over the batch. Use for cases where processing
    /// the batch is expected to throw before it reaches the database, so the loop's 10 second
    /// throttle delay completes immediately instead of stalling the test.
    /// </summary>
    public static void SetupReadsCancelingFirst(Mock<IDatabase> cache, CancellationTokenSource cts, StreamEntry[] batch)
    {
        StreamRead(cache).ReturnsAsync(() =>
        {
            cts.Cancel();
            return batch;
        });
    }

    /// <summary>Cancels <paramref name="cts"/> and then fails the read.</summary>
    public static void SetupReadThrows(Mock<IDatabase> cache, CancellationTokenSource cts, Exception ex)
    {
        StreamRead(cache).Returns(() =>
        {
            cts.Cancel();
            throw ex;
        });
    }

    /// <summary>
    /// Serves <paramref name="batches"/> and advances <paramref name="clock"/> by
    /// <paramref name="advance"/> before each one, so the loop's periodic metric window can be
    /// crossed deterministically.
    /// </summary>
    public static void SetupReadsAdvancingClock(Mock<IDatabase> cache, CancellationTokenSource cts,
        Microsoft.Extensions.Time.Testing.FakeTimeProvider clock, TimeSpan advance, params StreamEntry[][] batches)
    {
        var call = 0;
        StreamRead(cache).ReturnsAsync(() =>
        {
            if (call < batches.Length)
            {
                clock.Advance(advance);
                return batches[call++];
            }
            cts.Cancel();
            return [];
        });
    }

    private static Moq.Language.Flow.ISetup<IDatabase, Task<StreamEntry[]>> StreamRead(Mock<IDatabase> cache)
        => cache.Setup(x => x.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()));

    /// <summary>
    /// Runs a consumer loop to completion, absorbing the cancellation that stops it.
    /// </summary>
    /// <remarks>
    /// The failsafe token is a backstop, never reached on a healthy run: these loops finish in
    /// milliseconds. It exists because the only thing that stops them is the
    /// <c>cts.Cancel()</c> fired from inside the mocked stream read. If that setup ever stops
    /// matching the production call — a StackExchange.Redis overload change, or someone adding a
    /// <c>noAck:</c> or <c>count:</c> argument — Moq returns an empty array forever and the loop
    /// spins on a delay whose token is never canceled. Without this the result is a hung CI job
    /// instead of a red test.
    /// </remarks>
    public static async Task RunAsync(Func<CancellationToken, Task> execute, CancellationToken token)
    {
        using var failsafe = new CancellationTokenSource(FailsafeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, failsafe.Token);
        try
        {
            await execute(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // The loop's own throttle delay observes the canceled token; this is the normal exit.
        }

        Assert.IsFalse(failsafe.IsCancellationRequested,
            $"The consumer loop did not stop within {FailsafeTimeout.TotalSeconds:0}s. The mocked stream read " +
            "is no longer matching the production call, so the token that ends the loop was never canceled.");
    }

    /// <summary>Backstop for a loop that never observes its cancellation. See <see cref="RunAsync"/>.</summary>
    public static readonly TimeSpan FailsafeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A logger factory whose logger appends every formatted message to the returned list, together
    /// with the exception it was logged with. The exception matters: the consumer loops log the same
    /// "Error reading ... stream" text for a genuine processing failure and for the cancellation that
    /// stops them, so only the exception type distinguishes the two.
    /// </summary>
    public static (ILoggerFactory Factory, List<(LogLevel Level, string Message, Exception? Exception)> Messages) RecordingLoggerFactory()
    {
        var messages = new List<(LogLevel, string, Exception?)>();
        var logger = new Mock<ILogger>();
        logger.Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var exception = invocation.Arguments[3] as Exception;
                var formatter = invocation.Arguments[4];
                var message = formatter?.GetType().GetMethod("Invoke")?
                    .Invoke(formatter, [invocation.Arguments[2], exception])?.ToString() ?? string.Empty;
                lock (messages)
                {
                    messages.Add((level, message, exception));
                }
            }));

        var factory = new Mock<ILoggerFactory>();
        factory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(logger.Object);
        return (factory.Object, messages);
    }

    public static IConfiguration ConfigForEvent(int eventId)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", eventId.ToString() } })
            .Build();
}

internal sealed class TestableLogConsumerService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
    IConfiguration configuration, IDbContextFactory<TsContext> tsContext, HybridCache hcache, TimeProvider timeProvider)
    : LogConsumerService(loggerFactory, cacheMux, configuration, tsContext, hcache, timeProvider)
{
    public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
}

internal sealed class TestableEventProcessLogger(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
    IConfiguration configuration, IDbContextFactory<TsContext> tsContext, TimeProvider timeProvider)
    : EventProcessLogger(loggerFactory, cacheMux, configuration, tsContext, timeProvider)
{
    public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
}

internal sealed class TestableExternalMessageLogConsumer(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
    IConfiguration configuration, IDbContextFactory<TsContext> tsContext, TimeProvider timeProvider)
    : ExternalMessageLogConsumer(loggerFactory, cacheMux, configuration, tsContext, timeProvider)
{
    public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
}
