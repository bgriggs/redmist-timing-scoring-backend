using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

public class PassingInformation
{
    public string RegistrationNumber { get; set; } = string.Empty;
    public string LapTime { get; set; } = string.Empty;
    public DateTime LapTimestamp
    {
        get
        {
            DateTime.TryParseExact(LapTime, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result);
            return result;
        }
    }

    public string RaceTime { get; set; } = string.Empty;
    public DateTime RaceTimestamp
    {
        get
        {
            DateTime.TryParseExact(RaceTime, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result);
            return result;
        }
    }

    /// <summary>
    /// Processes $J messages.
    /// </summary>
    /// <example>$J,"1234BE","00:02:03.826","01:42:17.672"</example>
    public void ProcessJ(string[] parts)
    {
        RegistrationNumber = parts[1].Replace("\"", "");
        LapTime = parts[2].Replace("\"", "");
        RaceTime = parts[3].Replace("\"", "");
    }
}
