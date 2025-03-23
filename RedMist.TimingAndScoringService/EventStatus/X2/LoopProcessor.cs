using MediatR;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.EventStatus.X2;

/// <summary>
/// Receives and saves X2 loops to the database.
/// </summary>
public class LoopProcessor : INotificationHandler<X2LoopsNotification>
{
    private ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;


    public LoopProcessor(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }


    public async Task Handle(X2LoopsNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            // Insert or update loops in the database
            using var db = await tsContext.CreateDbContextAsync(cancellationToken);

            foreach (var loop in notification.Loops)
            {
                await db.X2Loops.Where(l => l.OrganizationId == loop.OrganizationId && l.EventId == loop.EventId && l.Id == loop.Id)
                    .ExecuteDeleteAsync(cancellationToken: cancellationToken);
            }

            await db.X2Loops.AddRangeAsync(notification.Loops, cancellationToken: cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing X2 loops");
        }
    }
}
