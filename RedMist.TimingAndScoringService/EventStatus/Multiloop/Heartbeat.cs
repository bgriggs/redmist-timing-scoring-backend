using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

/// <summary>
/// 
/// </summary>
/// <see cref="https://www.scribd.com/document/212233593/Multiloop-Timing-Protocol"/>
[Reactive]
public partial class Heartbeat : Message
{
    private readonly DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public partial string TrackStatus { get; private set; } = string.Empty;

    public Flags Flag { get; private set; }

    /// <summary>
    /// Time in seconds since 1/1/1970.
    /// </summary>
    public uint TimeDateSec { get; private set; }

    public DateTime TimeDate => epoch.AddSeconds(TimeDateSec);

    /// <summary>
    /// Elapsed time in milliseconds.
    /// </summary>
    public uint ElapsedTimeMs { get; private set; }
    public TimeSpan ElapsedTime => TimeSpan.FromMilliseconds(ElapsedTimeMs);

    public partial int LapsToGo { get; private set; }

    /// <summary>
    /// Time to go in milliseconds.
    /// </summary>
    public uint TimeToGoMs { get; private set; }
    public TimeSpan TimeToGo => TimeSpan.FromMilliseconds(TimeToGoMs);

    public bool IsDirty { get; private set; }


    public Heartbeat()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$H�N�2F4�Q1�G�685E9B2F�9A0AB�0�D42B4</example>
    public void ProcessH(string data)
    {
        var parts = ProcessHeader(data);
        TrackStatus = parts[4].Trim();
        Flag = ConvertTrackState(TrackStatus);

        if (uint.TryParse(parts[5], NumberStyles.HexNumber, null, out var td))
            TimeDateSec = td;
        if (uint.TryParse(parts[6], NumberStyles.HexNumber, null, out var et))
            ElapsedTimeMs = et;
        if (int.TryParse(parts[7], out var ltg))
            LapsToGo = ltg;
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var ttg))
            TimeToGoMs = ttg;
    }

    public static Flags ConvertTrackState(string ts)
    {
        return ts switch
        {
            "G" => Flags.Green,
            "Y" => Flags.Yellow,
            "R" => Flags.Red,
            "W" => Flags.White,
            "K" => Flags.Checkered,
            "U" => Flags.Unknown,
            "C" => Flags.Unknown,
            _ => Flags.Unknown,
        };
    }
}
