namespace RedMist.TimingAndScoringService.EventStatus;

public interface IDataProcessor
{
    Task ProcessUpdate(string data, CancellationToken stoppingToken);
}
