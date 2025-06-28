using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

[Reactive]
public partial class FlagInformation : Message
{
    public partial string TrackStatus { get; private set; } = string.Empty;
    public Flags Flag { get; private set; }
    public partial ushort LapNumber { get; private set; }
    public partial uint GreenTimeMs { get; private set; }
    public TimeSpan GreenTime => TimeSpan.FromMilliseconds(GreenTimeMs);
    public partial ushort GreenLaps { get; private set; }
    public partial uint YellowTimeMs { get; private set; }
    public TimeSpan YellowTime => TimeSpan.FromMilliseconds(YellowTimeMs);
    public partial ushort YellowLaps { get; private set; }
    public partial uint RedTimeMs { get; private set; }
    public TimeSpan RedTime => TimeSpan.FromMilliseconds(RedTimeMs);
    public partial ushort NumberOfYellows { get; private set; }
    public partial string CurrentLeader { get; private set; } = string.Empty;
    public partial ushort LeadChanges { get; private set; }
    public partial string AverageRaceSpeedMph { get; private set; } = string.Empty;

    public bool IsDirty { get; private set; }


    public FlagInformation()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$F�R�5�Q1�K�0�D7108�6�6088A�1�0�1�00�1�81.63</example>
    public void ProcessF(string data)
    {
        var parts = ProcessHeader(data);

        // TrackStatus
        TrackStatus = parts[4].Trim();
        Flag = Heartbeat.ConvertTrackState(TrackStatus);

        // LapNumber
        if (ushort.TryParse(parts[5], NumberStyles.HexNumber, null, out var ln))
            LapNumber = ln;

        // GreenTimeMs
        if (uint.TryParse(parts[6], NumberStyles.HexNumber, null, out var gt))
            GreenTimeMs = gt;

        // GreenLaps
        if (ushort.TryParse(parts[7], NumberStyles.HexNumber, null, out var gl))
            GreenLaps = gl;

        // YellowTimeMs
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var yt))
            YellowTimeMs = yt;

        // YellowLaps
        if (ushort.TryParse(parts[9], NumberStyles.HexNumber, null, out var yl))
            YellowLaps = yl;

        // RedTimeMs
        if (uint.TryParse(parts[10], NumberStyles.HexNumber, null, out var rt))
            RedTimeMs = rt;

        // NumberOfYellows
        if (ushort.TryParse(parts[11], NumberStyles.HexNumber, null, out var ny))
            NumberOfYellows = ny;

        // CurrentLeader
        CurrentLeader = parts[12].Trim();

        // LeadChanges
        if (ushort.TryParse(parts[13], NumberStyles.HexNumber, null, out var lc))
            LeadChanges = lc;

        // AverageRaceSpeedMph
        AverageRaceSpeedMph = parts[14].Trim();
    }
}
