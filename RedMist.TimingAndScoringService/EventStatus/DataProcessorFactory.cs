using MediatR;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;

namespace RedMist.TimingAndScoringService.EventStatus;

public class DataProcessorFactory : IDataProcessorFactory
{
    private readonly IMediator mediator;
    private readonly ILoggerFactory loggerFactory;

    public DataProcessorFactory(IMediator mediator, ILoggerFactory loggerFactory)
    {
        this.mediator = mediator;
        this.loggerFactory = loggerFactory;
    }

    public IDataProcessor CreateDataProcessor(string type, int eventId)
    {
        if (string.Compare(type, "RMonitor", StringComparison.OrdinalIgnoreCase) == 0)
        {
            return new RmDataProcessor(eventId, mediator, loggerFactory);
        }
        throw new NotImplementedException();
    }
}
