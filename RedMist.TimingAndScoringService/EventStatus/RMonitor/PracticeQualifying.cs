using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

[Reactive]
public partial class PracticeQualifying
{
    public partial int Position { get; set; }

    [IgnoreReactive]
    public string RegistrationNumber { get; set; } = string.Empty;
    public partial int BestLap { get; set; }
    public partial string BestLapTime { get; set; } = string.Empty;

    [IgnoreReactive]
    public DateTime BestTimeTimestamp
    {
        get
        {
            DateTime.TryParseExact(BestLapTime, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result);
            return result;
        }
    }

    public bool IsDirty { get; set; }


    public PracticeQualifying()
    {
        PropertyChanged += (sender, args) => IsDirty = true;
    }

    /// <summary>
    /// Processes $H messages.
    /// </summary>
    /// <example>$H,2,"1234BE",3,"00:02:17.872"</example>
    public ISessionStateChange? ProcessH(string[] parts)
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
