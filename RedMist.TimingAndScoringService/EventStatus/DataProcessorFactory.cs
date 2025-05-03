using MediatR;
using RedMist.TimingAndScoringService.EventStatus.X2;

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


    public IDataProcessor CreateDataProcessor(string type, int eventId, SessionMonitor sessionMonitor, PitProcessor pitProcessor, FlagProcessor flagProcessor, CompetitorMetadataProcessor competitorMetadataProcessor)
    {
        if (string.Compare(type, "RMonitor", StringComparison.OrdinalIgnoreCase) == 0)
        {
            return new OrbitsDataProcessor(eventId, mediator, loggerFactory, sessionMonitor, pitProcessor, flagProcessor, competitorMetadataProcessor);
        }
        throw new NotImplementedException();
    }
}
