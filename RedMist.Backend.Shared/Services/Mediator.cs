using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RedMist.Backend.Shared.Services;

/// <summary>
/// Simple mediator implementation for publishing notifications to registered handlers.
/// </summary>
public class Mediator : IMediator
{
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<Mediator> logger;

    public Mediator(IServiceProvider serviceProvider, ILogger<Mediator> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    public async Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification
    {
        if (notification == null)
        {
            logger.LogWarning("Attempted to publish null notification of type {Type}", typeof(T).Name);
            return;
        }

        var handlers = serviceProvider.GetServices<INotificationHandler<T>>();
        var tasks = new List<Task>();

        foreach (var handler in handlers)
        {
            try
            {
                tasks.Add(handler.Handle(notification, cancellationToken));
                logger.LogTrace("Publishing notification {NotificationType} to handler {HandlerType}", 
                    typeof(T).Name, handler.GetType().Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating task for handler {HandlerType} with notification {NotificationType}", 
                    handler.GetType().Name, typeof(T).Name);
            }
        }

        if (tasks.Count == 0)
        {
            logger.LogDebug("No handlers found for notification type {Type}", typeof(T).Name);
            return;
        }

        try
        {
            await Task.WhenAll(tasks);
            logger.LogTrace("Successfully published notification {NotificationType} to {HandlerCount} handlers", 
                typeof(T).Name, tasks.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error publishing notification {NotificationType}", typeof(T).Name);
            throw;
        }
    }
}