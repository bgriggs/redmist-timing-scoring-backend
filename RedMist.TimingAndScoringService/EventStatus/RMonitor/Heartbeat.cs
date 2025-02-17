namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

[Reactive]
public partial class Heartbeat
{
    public partial int LapsToGo { get; set; }
    [IgnoreReactive]
    public string TimeToGo { get; set; } = string.Empty;
    [IgnoreReactive]
    public string TimeOfDay { get; set; } = string.Empty;
    [IgnoreReactive]
    public string RaceTime { get; set; } = string.Empty;
    public partial string FlagStatus { get; set; } = string.Empty;

    public bool IsDirty { get; set; }


    public Heartbeat()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// Processes $F –- Heartbeat message
    /// </summary>
    /// <example>$F,14,"00:12:45","13:34:23","00:09:47","Green "</example>
    public void ProcessF(string data)
    {
        var parts = data.Split(',');
        if (parts.Length != 6)
            return;

        if (int.TryParse(parts[1], out var ltt))
        {
            LapsToGo = ltt;
        }
        else
        {
            LapsToGo = 0;
        }
        TimeToGo = parts[2].Replace("\"", "").Trim();
        TimeOfDay = parts[3].Replace("\"", "").Trim();
        RaceTime = parts[4].Replace("\"", "").Trim();
        FlagStatus = parts[5].Replace("\"", "").Trim();
    }
}
