using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

[Reactive]
public partial class LineCrossing : Message
{
    public partial string Number { get; private set; } = string.Empty;
    public partial uint UniqueIdentifier { get; private set; }
    public partial string TimeLine { get; private set; } = string.Empty;
    public partial string SourceStr { get; private set; } = string.Empty;
    public LineCorssingSource Source => SourceStr switch
    {
        "A" => LineCorssingSource.Antenna,
        "M" => LineCorssingSource.Manual,
        _ => LineCorssingSource.Photocell,
    };
    public partial uint ElaspedTimeMs { get; private set; }
    public TimeSpan ElaspedTime => TimeSpan.FromMilliseconds(ElaspedTimeMs);
    public partial string TrackStatus { get; private set; } = string.Empty;
    public Flags Flag { get; private set; }
    public partial string CrossingStatusStr { get; private set; } = string.Empty;
    public LineCrossingStatus CrossingStatus => CrossingStatusStr switch
    {
        "P" => LineCrossingStatus.Pit,
        _ => LineCrossingStatus.Track,
    };

    public bool IsDirty { get; private set; }


    public LineCrossing()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$L�N�EF325�Q1�89�5�SF�A�9B82E�G�T</example>
    public void ProcessL(string data)
    {
        var parts = ProcessHeader(data);

        // Number
        Number = parts[4].Trim();

        // UniqueIdentifier
        if (uint.TryParse(parts[5], NumberStyles.HexNumber, null, out var ui))
            UniqueIdentifier = ui;

        // TimeLine
        TimeLine = parts[6].Trim();

        // SourceStr
        SourceStr = parts[7].Trim();

        // ElaspedTimeMs
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var et))
            ElaspedTimeMs = et;

        // TrackStatus
        TrackStatus = parts[9].Trim();
        Flag = Heartbeat.ConvertTrackState(TrackStatus);

        // CrossingStatus
        CrossingStatusStr = parts[10].Trim();
    }
}
