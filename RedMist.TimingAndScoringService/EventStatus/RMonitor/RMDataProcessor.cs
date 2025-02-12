using MediatR;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

public class RmDataProcessor : IDataProcessor
{
    private readonly IMediator mediator;

    public RmDataProcessor(IMediator mediator)
    {
        this.mediator = mediator;
    }

    public async Task ProcessUpdate(string data, CancellationToken stoppingToken = default)
    {
        // Parse data and send to mediator


        await mediator.Publish(new StatusNotification(1, data), stoppingToken);
    }
}
