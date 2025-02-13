using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

public class RaceInformation
{
    public int Position { get; set; }
    public string RegistrationNumber { get; set; } = string.Empty;
    public int Laps { get; set; }
    public string RaceTime { get; set; } = string.Empty;
    public DateTime Timestamp
    {
        get 
        { 
            DateTime.TryParseExact(RaceTime, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result);
            return result;
        }
    }

    /// <summary>
    /// Processes $G messages.
    /// </summary>
    /// <example>$G,3,"1234BE",14,"01:12:47.872"</example>
    public void ProcessG(string[] parts)
    {
        Position = int.Parse(parts[1]);
        RegistrationNumber = parts[2].Replace("\"", "");
        Laps = int.Parse(parts[3]);
        RaceTime = parts[4].Replace("\"", "");
    }
}
