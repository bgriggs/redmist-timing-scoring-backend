namespace RedMist.ControlLogs;

public interface IControlLogFactory
{
    IControlLog CreateControlLog(string type);
}
