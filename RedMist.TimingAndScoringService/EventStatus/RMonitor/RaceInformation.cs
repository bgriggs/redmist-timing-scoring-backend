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
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// Processes $G messages.
    /// </summary>
    /// <example>$G,3,"1234BE",14,"01:12:47.872"</example>
    public void ProcessG(string[] parts)
    {
        Position = int.Parse(parts[1]);
        RegistrationNumber = parts[2].Replace("\"", "");
        if (int.TryParse(parts[3], out var l))
        {
            Laps = l;
        }
        else
        {
            Laps = 0;
        }
        RaceTime = parts[4].Replace("\"", "");
    }
}
