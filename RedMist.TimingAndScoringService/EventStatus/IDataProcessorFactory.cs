using RedMist.TimingAndScoringService.EventStatus.RMonitor;

namespace RedMist.TimingAndScoringService.EventStatus;

public interface IDataProcessorFactory
{
    IDataProcessor CreateDataProcessor(string type, int eventId, SessionMonitor sessionMonitor);
}
