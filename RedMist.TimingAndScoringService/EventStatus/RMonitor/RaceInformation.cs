using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

[Reactive]
public partial class RaceInformation
{
    public partial int Position { get; set; }

    [IgnoreReactive]
    public string RegistrationNumber { get; set; } = string.Empty;
    public partial int Laps { get; set; }
    public partial string RaceTime { get; set; } = string.Empty;

    [IgnoreReactive]
    public DateTime Timestamp
    {
        get
        {
            DateTime.TryParseExact(RaceTime, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result);
            return result;
        }
    }
    public bool IsDirty { get; set; }


    public RaceInformation()
    {
        PropertyChanged += (sender, args) => IsDirty = true;
    }


    /// <summary>
    /// Processes $G messages.
    /// </summary>
    /// <example>$G,3,"1234BE",14,"01:12:47.872"</example>
    public ICarStateChange? ProcessG(string[] parts)
    {
        var lastPosition = Position;
        Position = int.Parse(parts[1]);
        RegistrationNumber = parts[2].Replace("\"", "").Trim();
        var lastLap = Laps;
        if (int.TryParse(parts[3], out var l))
        {
            Laps = l;
        }
        else
        {
            Laps = 0;
        }
        RaceTime = parts[4].Replace("\"", "").Trim();

        // Check for changes, ignore race time as warranting an update
        if (lastLap != Laps || lastPosition != Position)
        {
            return new CarLapStateUpdate(this);
        }
        return null;
    }
}
