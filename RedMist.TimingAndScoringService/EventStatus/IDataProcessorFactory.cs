using RedMist.TimingAndScoringService.EventStatus.X2;

namespace RedMist.TimingAndScoringService.EventStatus;

public interface IDataProcessorFactory
{
    IDataProcessor CreateDataProcessor(string type, int eventId, SessionMonitor sessionMonitor, PitProcessor pitProcessor);
}
