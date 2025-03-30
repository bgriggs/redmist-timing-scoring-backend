using MediatR;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.EventStatus.X2;

public class PassingsProcessor : INotificationHandler<X2PassingsNotification>
{
    private ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;


    public PassingsProcessor(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }


    public async Task Handle(X2PassingsNotification notification, CancellationToken cancellationToken)
    {
        using var db = await tsContext.CreateDbContextAsync(cancellationToken);

        foreach (var passing in notification.Passings)
        {
            try
            {
                await db.AddAsync(passing, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
            }
            catch(Exception ex)
            {
                Logger.LogError(ex, "Error processing X2 passings");
            }
        }
    }
}
