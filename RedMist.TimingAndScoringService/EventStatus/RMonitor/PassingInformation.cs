using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

[Reactive]
public partial class PassingInformation
{
    [IgnoreReactive]
    public string RegistrationNumber { get; set; } = string.Empty;
    public partial string LapTime { get; set; } = string.Empty;
    
    [IgnoreReactive]
    public DateTime LapTimestamp
    {
        get
        {
            DateTime.TryParseExact(LapTime, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result);
            return result;
        }
    }

    public partial string RaceTime { get; set; } = string.Empty;

    [IgnoreReactive]
    public DateTime RaceTimestamp
    {
        get
        {
            DateTime.TryParseExact(RaceTime, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result);
            return result;
        }
    }

    public bool IsDirty { get; set; }


    public PassingInformation()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// Processes $J messages.
    /// </summary>
    /// <example>$J,"1234BE","00:02:03.826","01:42:17.672"</example>
    public void ProcessJ(string[] parts)
    {
        RegistrationNumber = parts[1].Replace("\"", "").Trim();
        LapTime = parts[2].Replace("\"", "").Trim();
        RaceTime = parts[3].Replace("\"", "").Trim();
    }
}
