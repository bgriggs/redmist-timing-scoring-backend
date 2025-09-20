using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

public class LineCrossing : Message
{
    public string Number { get; private set; } = string.Empty;
    public uint UniqueIdentifier { get; private set; }
    public string TimeLine { get; private set; } = string.Empty;
    public string SourceStr { get; private set; } = string.Empty;
    public LineCrossingSource Source => SourceStr switch
    {
        "A" => LineCrossingSource.Antenna,
        "M" => LineCrossingSource.Manual,
        _ => LineCrossingSource.Photocell,
    };
    public uint ElapsedTimeMs { get; private set; }
    public TimeSpan ElapsedTime => TimeSpan.FromMilliseconds(ElapsedTimeMs);
    public string TrackStatus { get; private set; } = string.Empty;
    public Flags Flag { get; private set; }
    public string CrossingStatusStr { get; private set; } = string.Empty;
    public LineCrossingStatus CrossingStatus => CrossingStatusStr switch
    {
        "P" => LineCrossingStatus.Pit,
        _ => LineCrossingStatus.Track,
    };


    /// <summary>
    /// Gets the car number from a line crossing message without fully processing it.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>car number or empty if the data is malformed</returns>
    public static string GetNumber(string data)
    {
        var parts = data.Split(Consts.DELIM);
        return parts.Length > 4 ? parts[4].Trim() : string.Empty;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <example>$L�N�EF325�Q1�89�5�SF�A�9B82E�G�T</example>
    public List<ICarStateChange> ProcessL(string data)
    {
        var parts = ProcessHeader(data);
        var changes = new List<ICarStateChange>();

        // Number
        Number = parts[4].Trim();

        // UniqueIdentifier
        if (uint.TryParse(parts[5], NumberStyles.HexNumber, null, out var ui))
            UniqueIdentifier = ui;

        // TimeLine
        TimeLine = parts[6].Trim();

        // SourceStr
        SourceStr = parts[7].Trim();

        // ElapsedTimeMs
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var et))
            ElapsedTimeMs = et;

        // TrackStatus
        TrackStatus = parts[9].Trim();
        Flag = Heartbeat.ConvertTrackState(TrackStatus);

        // CrossingStatus
        var cs = parts[10].Trim();
        if (cs != CrossingStatusStr)
        {
            CrossingStatusStr = cs;
            changes.Add(new PitSfCrossingStateUpdate(this));
        }

        return changes;
    }
}
