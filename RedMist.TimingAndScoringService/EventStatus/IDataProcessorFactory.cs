namespace RedMist.TimingAndScoringService.EventStatus;

public interface IDataProcessorFactory
{
    IDataProcessor CreateDataProcessor(string type);
}
