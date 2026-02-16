using RedMist.TimingCommon.Models;
using System.Text.RegularExpressions;

namespace RedMist.ControlLogs;

public interface IControlLog : IDisposable
{
    string Type { get; }

    /// <summary>
    /// Regex pattern for matching warning penalties.
    /// </summary>
    Regex WarningPattern { get; }

    /// <summary>
    /// Regex pattern for matching lap penalties. Must have a capture group for the number of laps.
    /// </summary>
    Regex LapPenaltyPattern { get; }

    /// <summary>
    /// Regex pattern for matching black flag penalties.
    /// </summary>
    Regex BlackFlagPattern { get; }

    Task<(bool success, IEnumerable<ControlLogEntry> logs)> LoadControlLogAsync(string parameter, CancellationToken stoppingToken = default);
}
