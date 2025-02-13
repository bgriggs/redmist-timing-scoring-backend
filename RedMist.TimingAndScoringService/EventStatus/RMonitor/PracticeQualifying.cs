using RedMist.TimingCommon.Models;
using System;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

public class PracticeQualifying
{
    public int Position { get; set; }
    public string RegistrationNumber { get; set; } = string.Empty;
    public int BestLap { get; set; }
    public string BestLapTime { get; set; } = string.Empty;
    public DateTime BestTimeTimestamp
    {
        get
        {
            DateTime.TryParseExact(BestLapTime, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result);
            return result;
        }
    }

    /// <summary>
    /// Processes $H messages.
    /// </summary>
    /// <example>$H,2,"1234BE",3,"00:02:17.872"</example>
    public void ProcessH(string[] parts)
    {
        Position = int.Parse(parts[1]);
        RegistrationNumber = parts[2].Replace("\"", "");
        BestLap = int.Parse(parts[3]);
        BestLapTime = parts[4].Replace("\"", "");
    }
}
