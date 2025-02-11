namespace RedMist.TimingAndScoringService.EventStatus;

public interface IDataProcessor
{
    void ProcessUpdate(string data);
}
