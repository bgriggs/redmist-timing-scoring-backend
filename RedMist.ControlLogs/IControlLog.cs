using RedMist.TimingCommon.Models;

namespace RedMist.ControlLogs;

public interface IControlLog
{
    string Type { get; }
    Task<(bool success, IEnumerable<ControlLogEntry> logs)> LoadControlLogAsync(string parameter, CancellationToken stoppingToken = default);
}
