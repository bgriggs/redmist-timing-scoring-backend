namespace RedMist.Backend.Shared.Services;

/// <summary>
/// Mediator interface for sending notifications across the application.
/// </summary>
public interface IMediator
{
    /// <summary>
    /// Publishes a notification to all registered handlers.
    /// </summary>
    /// <typeparam name="T">The notification type</typeparam>
    /// <param name="notification">The notification to publish</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification;
}

/// <summary>
/// Marker interface for notifications.
/// </summary>
public interface INotification
{
}

/// <summary>
/// Interface for notification handlers.
/// </summary>
/// <typeparam name="T">The notification type</typeparam>
public interface INotificationHandler<in T> where T : INotification
{
    /// <summary>
    /// Handles the notification.
    /// </summary>
    /// <param name="notification">The notification to handle</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task Handle(T notification, CancellationToken cancellationToken);
}