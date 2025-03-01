namespace RedMist.TimingAndScoringService.EventStatus.ControlLog;

public interface IControlLogFactory
{
    IControlLog CreateControlLog(string type);
}
