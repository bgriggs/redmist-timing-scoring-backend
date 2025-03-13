using MediatR;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Receives status updates and forwards them to a logger to put them in the database.
/// </summary>
public class LogAggregator : INotificationHandler<StatusNotification>
{
    private readonly LapLogger lapLogger;
    private ILogger Logger { get; }


    public LogAggregator(ILoggerFactory loggerFactory, LapLogger lapLogger)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.lapLogger = lapLogger;
    }


    public Task Handle(StatusNotification notification, CancellationToken cancellationToken)
    {
        if (notification.Payload is not null)
        {
            var carPositions = notification.Payload.CarPositions.Concat(notification.Payload.CarPositionUpdates).ToList();
            if (carPositions.Count == 0)
                return Task.CompletedTask;

            _ = Task.Run(async () =>
            {
                try
                {
                    await lapLogger.LogCarPositionUpdates(notification.EventId, notification.SessionId, carPositions, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error logging car position updates: {0}", ex.Message);
                }
            }, cancellationToken);
        }
        return Task.CompletedTask;
    }
}
