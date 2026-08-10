using BigMission.TestHelpers.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RedMist.Backend.Shared.Services;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The in-process notification bus every service publishes through. What matters is that one broken
/// handler cannot take the others down, and that a genuine failure is not swallowed.
/// </summary>
[TestClass]
public class MediatorTests
{
    public sealed record CarUpdated(string CarNumber) : INotification;

    public sealed record SessionEnded : INotification;

    /// <summary>Records the notifications it receives; used to assert fan-out.</summary>
    public sealed class RecordingHandler : INotificationHandler<CarUpdated>
    {
        public List<CarUpdated> Received { get; } = [];
        public CancellationToken LastToken { get; private set; }

        public Task Handle(CarUpdated notification, CancellationToken cancellationToken)
        {
            Received.Add(notification);
            LastToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>Throws before returning a task, i.e. fails while the mediator is still building the list.</summary>
    private sealed class SynchronouslyThrowingHandler : INotificationHandler<CarUpdated>
    {
        public Task Handle(CarUpdated notification, CancellationToken cancellationToken)
            => throw new InvalidOperationException("handler exploded");
    }

    /// <summary>Returns a task that faults later, i.e. fails while the mediator is awaiting.</summary>
    private sealed class AsynchronouslyFaultingHandler : INotificationHandler<CarUpdated>
    {
        public async Task Handle(CarUpdated notification, CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("handler exploded later");
        }
    }

    private readonly List<ServiceProvider> providers = [];

    [TestCleanup]
    public void DisposeProviders() => providers.ForEach(p => p.Dispose());

    private Mediator CreateMediator(params INotificationHandler<CarUpdated>[] handlers)
    {
        var services = new ServiceCollection();
        foreach (var handler in handlers)
        {
            services.AddSingleton(handler);
        }
        var provider = services.BuildServiceProvider();
        providers.Add(provider);
        return new Mediator(provider, new DebugLoggerFactory().CreateLogger<Mediator>());
    }

    /// <summary>
    /// Notifications are published from hot paths whether or not anything subscribes to them, so an
    /// unsubscribed type must be cheap and silent rather than an error.
    /// </summary>
    [TestMethod]
    public async Task Publish_WithNoHandlersRegistered_IsANoOp()
    {
        var mediator = CreateMediator();

        await mediator.Publish(new CarUpdated("42"));
    }

    [TestMethod]
    public async Task Publish_ReachesEveryHandlerRegisteredForTheNotification()
    {
        var first = new RecordingHandler();
        var second = new RecordingHandler();
        var mediator = CreateMediator(first, second);

        await mediator.Publish(new CarUpdated("42"));

        Assert.AreEqual("42", first.Received.Single().CarNumber);
        Assert.AreEqual("42", second.Received.Single().CarNumber);
    }

    [TestMethod]
    public async Task Publish_ForwardsTheCancellationTokenToHandlers()
    {
        var handler = new RecordingHandler();
        var mediator = CreateMediator(handler);
        using var cts = new CancellationTokenSource();

        await mediator.Publish(new CarUpdated("42"), cts.Token);

        Assert.AreEqual(cts.Token, handler.LastToken);
    }

    [TestMethod]
    public async Task Publish_DoesNotReachHandlersForOtherNotificationTypes()
    {
        var handler = new RecordingHandler();
        var mediator = CreateMediator(handler);

        await mediator.Publish(new SessionEnded());

        Assert.IsEmpty(handler.Received);
    }

    [TestMethod]
    public async Task Publish_WithANullNotification_DoesNotReachHandlers()
    {
        var handler = new RecordingHandler();
        var mediator = CreateMediator(handler);

        await mediator.Publish<CarUpdated>(null!);

        Assert.IsEmpty(handler.Received);
    }

    /// <summary>
    /// A handler that throws before it returns a task is contained, so the remaining handlers still
    /// run. Publishers rely on this: a broken subscriber must not stop the rest of the pipeline.
    /// </summary>
    [TestMethod]
    public async Task Publish_WhenAHandlerThrowsBeforeReturningATask_TheOtherHandlersStillRun()
    {
        var survivor = new RecordingHandler();
        var mediator = CreateMediator(new SynchronouslyThrowingHandler(), survivor);

        await mediator.Publish(new CarUpdated("42"));

        Assert.AreEqual("42", survivor.Received.Single().CarNumber);
    }

    /// <summary>
    /// The asymmetric half of the behavior above: a handler whose task faults is not contained, it
    /// propagates to the publisher.
    /// </summary>
    [TestMethod]
    public async Task Publish_WhenAHandlersTaskFaults_TheFailureReachesThePublisher()
    {
        var survivor = new RecordingHandler();
        var mediator = CreateMediator(new AsynchronouslyFaultingHandler(), survivor);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Publish(new CarUpdated("42")));

        Assert.AreEqual("42", survivor.Received.Single().CarNumber, "handlers are started before any of them is awaited");
    }
}

/// <summary>
/// Covers the assembly scanning that wires handlers up, since a handler that is silently not
/// discovered looks exactly like one that is never notified.
/// </summary>
[TestClass]
public class MediatorExtensionsTests
{
    public sealed record ScannedNotification : INotification;

    public sealed record OtherScannedNotification : INotification;

    /// <summary>Discovered by the scan; handles two notification types through two interfaces.</summary>
    public sealed class MultiNotificationHandler : INotificationHandler<ScannedNotification>, INotificationHandler<OtherScannedNotification>
    {
        public Task Handle(ScannedNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Handle(OtherScannedNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Must be skipped by the scan even though it implements the handler interface.</summary>
    public abstract class AbstractScannedHandler : INotificationHandler<ScannedNotification>
    {
        public Task Handle(ScannedNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [TestMethod]
    public void AddMediator_RegistersTheMediatorAndDiscoversHandlersInTheGivenAssembly()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMediator(typeof(MediatorExtensionsTests).Assembly)
            .BuildServiceProvider();

        Assert.IsInstanceOfType<Mediator>(provider.GetRequiredService<IMediator>());
        Assert.HasCount(1, provider.GetServices<INotificationHandler<ScannedNotification>>()
            .OfType<MultiNotificationHandler>().ToList());
    }

    [TestMethod]
    public void AddMediator_RegistersAHandlerUnderEachNotificationTypeItHandles()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMediator(typeof(MediatorExtensionsTests).Assembly)
            .BuildServiceProvider();

        Assert.HasCount(1, provider.GetServices<INotificationHandler<OtherScannedNotification>>()
            .OfType<MultiNotificationHandler>().ToList());
    }

    [TestMethod]
    public void AddMediator_SkipsAbstractHandlerTypes()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMediator(typeof(MediatorExtensionsTests).Assembly)
            .BuildServiceProvider();

        // Resolving would throw if an abstract type had been registered as an implementation.
        Assert.HasCount(1, provider.GetServices<INotificationHandler<ScannedNotification>>().ToList());
    }

    /// <summary>
    /// Services pass several marker types that often live in the same assembly. Without the distinct
    /// the scan would register, and the mediator would then invoke, every handler twice.
    /// </summary>
    [TestMethod]
    public void AddMediatorFromAssembliesContaining_WithTwoMarkersFromOneAssembly_RegistersEachHandlerOnce()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMediatorFromAssembliesContaining(typeof(MediatorExtensionsTests), typeof(MediatorTests))
            .BuildServiceProvider();

        Assert.HasCount(1, provider.GetServices<INotificationHandler<ScannedNotification>>()
            .OfType<MultiNotificationHandler>().ToList());
    }

    [TestMethod]
    public void AddMediatorFromAssemblyContaining_DiscoversHandlersInTheMarkersAssembly()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMediatorFromAssemblyContaining<MediatorExtensionsTests>()
            .BuildServiceProvider();

        Assert.HasCount(1, provider.GetServices<INotificationHandler<ScannedNotification>>()
            .OfType<MultiNotificationHandler>().ToList());
    }

    [TestMethod]
    public void AddMediator_WithNoAssemblies_StillRegistersTheMediator()
    {
        using var provider = new ServiceCollection().AddLogging().AddMediator().BuildServiceProvider();

        Assert.IsInstanceOfType<Mediator>(provider.GetRequiredService<IMediator>());
    }
}
