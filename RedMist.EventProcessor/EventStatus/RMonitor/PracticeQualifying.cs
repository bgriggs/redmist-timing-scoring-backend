using RedMist.Backend.Shared.Utilities;
using RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;

namespace RedMist.EventProcessor.EventStatus.RMonitor;

[Reactive]
public partial class PracticeQualifying
{
    public partial int Position { get; set; }

    [IgnoreReactive]
    public string RegistrationNumber { get; set; } = string.Empty;
    public partial int BestLap { get; set; }
    public partial string BestLapTime { get; set; } = string.Empty;

    [IgnoreReactive]
    public TimeSpan BestTimeTimestamp => RaceTimeParser.Parse(BestLapTime);

    public bool IsDirty { get; set; }


    public PracticeQualifying()
    {
        PropertyChanged += (sender, args) => IsDirty = true;
    }

    /// <summary>
    /// Processes $H messages.
    /// </summary>
    /// <example>$H,2,"1234BE",3,"00:02:17.872"</example>
    public ICarStateChange? ProcessH(string[] parts)
    {
        Position = int.Parse(parts[1]);
        RegistrationNumber = parts[2].Replace("\"", "").Trim();

        var lastBestLap = BestLap;
        BestLap = int.Parse(parts[3]);

        var lastBestLapTime = BestLapTime;
        BestLapTime = parts[4].Replace("\"", "").Trim();

        if (lastBestLap != BestLap || lastBestLapTime != BestLapTime)
        {
            return new CarBestLapStateUpdate(this);
        }

        return null;
    }
}
