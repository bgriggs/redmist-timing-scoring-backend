using MediatR;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;

namespace RedMist.TimingAndScoringService.EventStatus
{
    public class DataProcessorFactory : IDataProcessorFactory
    {
        private readonly IMediator mediator;

        public DataProcessorFactory(IMediator mediator)
        {
            this.mediator = mediator;
        }

        public IDataProcessor CreateDataProcessor(string type)
        {
            if (string.Compare(type, "RMonitor", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return new RmDataProcessor(mediator);
            }
            throw new NotImplementedException();
        }
    }
}
