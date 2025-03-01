using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.ControlLog;

public interface IControlLog
{
    string Type { get; }
    Task<IEnumerable<ControlLogEntry>> LoadControlLogAsync(string parameter, CancellationToken stoppingToken = default);
}
