using RedMist.TimingAndScoringService.EventStatus.RMonitor;

namespace RedMist.TimingAndScoringService.EventStatus
{
    public class DataProcessorFactory : IDataProcessorFactory
    {
        public IDataProcessor CreateDataProcessor(string type)
        {
            if (type == "RMonitor")
            {
                return new RmDataProcessor();
            }
            throw new NotImplementedException();
        }
    }
}
