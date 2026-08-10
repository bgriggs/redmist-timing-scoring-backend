using RedMist.Backend.Shared.Utilities;
using RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;

namespace RedMist.EventProcessor.EventStatus.RMonitor;

[Reactive]
public partial class PassingInformation
{
    [IgnoreReactive]
    public string RegistrationNumber { get; set; } = string.Empty;
    public partial string LapTime { get; set; } = string.Empty;

    [IgnoreReactive]
    public TimeSpan LapTimestamp => RaceTimeParser.Parse(LapTime);

    public partial string RaceTime { get; set; } = string.Empty;

    /// <summary>
    /// The car's elapsed race time. Unbounded hours - this passes 24 hours in an endurance event.
    /// </summary>
    [IgnoreReactive]
    public TimeSpan RaceTimestamp => RaceTimeParser.Parse(RaceTime);

    public bool IsDirty { get; set; }


    public PassingInformation()
    {
        PropertyChanged += (sender, args) => IsDirty = true;
    }


    /// <summary>
    /// Processes $J messages.
    /// </summary>
    /// <example>$J,"1234BE","00:02:03.826","01:42:17.672"</example>
    public ICarStateChange? ProcessJ(string[] parts)
    {
        RegistrationNumber = parts[1].Replace("\"", "").Trim();

        var lastLapTime = LapTime;
        LapTime = parts[2].Replace("\"", "").Trim();
        RaceTime = parts[3].Replace("\"", "").Trim();

        // Check for changed lap time, ignore race time as not warranting a direction update.
        if (lastLapTime != LapTime)
        {
            return new CarLapTimeStateUpdate(this);
        }
        return null;
    }
}
