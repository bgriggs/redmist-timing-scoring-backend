using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Represents a processor that handles incoming timing data from a source timing system, e.g. Orbits (result monitor data).
/// </summary>
public interface IDataProcessor
{
    int EventId { get; }
    int SessionId { get; }
    PitProcessor? PitProcessor { get; }
    Task ProcessUpdate(string type, string data, int sessionId, CancellationToken stoppingToken);
    Task<Payload> GetPayload(CancellationToken stoppingToken);
}
