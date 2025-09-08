using RedMist.Backend.Shared.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Holds context information shared across the processing pipeline.
/// </summary>
public class SessionContext
{
    public SessionState SessionState { get; } = new SessionState();
    private readonly AsyncReaderWriterLock sessionStateLock = new();
    public AsyncReaderWriterLock SessionStateLock => sessionStateLock;

    public int EventId { get; }

    public virtual CancellationToken CancellationToken { get; set; }
    public virtual bool IsMultiloopActive { get; set; }

    private readonly Dictionary<string, CarPosition> numberToCarPositionLookup = [];
    private readonly Dictionary<uint, string> transponderToNumberLookup = [];


    public SessionContext(IConfiguration configuration)
    {
        EventId = configuration.GetValue("event_id", 0);
    }


    public virtual async Task SetCarPositions(IEnumerable<CarPosition> carPositions)
    {
        using (await SessionStateLock.AcquireWriteLockAsync())
        {
            SessionState.CarPositions = [.. carPositions];

            transponderToNumberLookup.Clear();
            numberToCarPositionLookup.Clear();
            foreach (var cp in carPositions)
            {
                if (string.IsNullOrEmpty(cp.Number))
                    continue;

                numberToCarPositionLookup[cp.Number!] = cp;

                if (cp.TransponderId != 0)
                {
                    transponderToNumberLookup[cp.TransponderId] = cp.Number!;
                }
            }
        }
    }

    public virtual CarPosition? GetCarByNumber(string carNumber)
    {
        if (numberToCarPositionLookup.TryGetValue(carNumber, out var carPosition))
        {
            return carPosition;
        }
        return null;
    }

    public virtual string? GetCarNumberForTransponder(uint transponderId)
    {
        if (transponderToNumberLookup.TryGetValue(transponderId, out var carNumber))
        {
            return carNumber;
        }
        return null;
    }
}
