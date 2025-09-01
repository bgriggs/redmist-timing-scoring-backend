using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

public class CompletedLap : Message
{
    public ushort Rank { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public uint UniqueIdentifier { get; private set; }
    public ushort CompletedLaps { get; private set; }
    public uint ElapsedTimeMs { get; private set; }
    public TimeSpan ElapsedTime => TimeSpan.FromMilliseconds(ElapsedTimeMs);
    public uint LastLapTimeMs { get; private set; }
    public TimeSpan LastLapTime => TimeSpan.FromMilliseconds(LastLapTimeMs);
    public string LapStatus { get; private set; } = string.Empty;
    public uint FastestLapTimeMs { get; private set; }
    public TimeSpan FastestLapTime => TimeSpan.FromMilliseconds(FastestLapTimeMs);
    public ushort FastestLap { get; private set; }
    public uint TimeBehindLeaderMs { get; private set; }
    public TimeSpan TimeBehindLeader => TimeSpan.FromMilliseconds(TimeBehindLeaderMs);
    public ushort LapsBehindLeader { get; private set; }
    public uint TimeBehindPrecedingCarMs { get; private set; }
    public TimeSpan TimeBehindPrecedingCar => TimeSpan.FromMilliseconds(TimeBehindPrecedingCarMs);
    public ushort LapsBehindPrecedingCar { get; private set; }
    public ushort OverallRank { get; private set; }
    public uint OverallBestLapTimeMs { get; private set; }
    public TimeSpan OverallBestLapTime => TimeSpan.FromMilliseconds(OverallBestLapTimeMs);
    public string CurrentStatus { get; private set; } = string.Empty;
    public string TrackStatus { get; private set; } = string.Empty;
    public Flags Flag { get; private set; }
    public ushort PitStopCount { get; private set; }
    public ushort LastLapPitted { get; private set; }
    public ushort StartPosition { get; private set; }
    public ushort LapsLed { get; private set; }


    /// <summary>
    /// Gets the car number from the message.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>Car number or empty if malformed message</returns>
    public static string GetNumber(string data)
    {
        var parts = data.Split(Consts.DELIM);
        return parts.Length > 5 ? parts[5] : string.Empty;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <example>$C�U�80004�Q1�C�0�8�4�83DDF�1CB83�T�1CB83�4�2E6A�0�649�0�C�1CB83�Unknown�G�1�0�9�0</example>
    public List<ISessionStateChange> ProcessC(string data)
    {
        var parts = ProcessHeader(data);
        var changes = new List<ISessionStateChange>();

        // Rank
        if (ushort.TryParse(parts[4], NumberStyles.HexNumber, null, out var r))
            Rank = r;

        // Number
        Number = parts[5];

        // UniqueIdentifier
        if (uint.TryParse(parts[6], NumberStyles.HexNumber, null, out var ui))
            UniqueIdentifier = ui;

        // CompletedLaps
        if (ushort.TryParse(parts[7], NumberStyles.HexNumber, null, out var cl))
            CompletedLaps = cl;

        // ElapsedTimeMs
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var et))
            ElapsedTimeMs = et;

        // LastLapTimeMs
        if (uint.TryParse(parts[9], NumberStyles.HexNumber, null, out var llt))
            LastLapTimeMs = llt;

        // LapStatus
        LapStatus = parts[10];

        // FastestLapTimeMs
        if (uint.TryParse(parts[11], NumberStyles.HexNumber, null, out var flt))
            FastestLapTimeMs = flt;

        // FastestLap
        if (ushort.TryParse(parts[12], NumberStyles.HexNumber, null, out var fl))
            FastestLap = fl;

        // TimeBehindLeaderMs
        if (uint.TryParse(parts[13], NumberStyles.HexNumber, null, out var tbl))
            TimeBehindLeaderMs = tbl;

        // LapsBehindLeader
        if (ushort.TryParse(parts[14], NumberStyles.HexNumber, null, out var lbl))
            LapsBehindLeader = lbl;

        // TimeBehindPrecedingCarMs
        if (uint.TryParse(parts[15], NumberStyles.HexNumber, null, out var tbpc))
            TimeBehindPrecedingCarMs = tbpc;

        // LapsBehindPrecedingCar
        if (ushort.TryParse(parts[16], NumberStyles.HexNumber, null, out var lbpc))
            LapsBehindPrecedingCar = lbpc;

        // OverallRank
        if (ushort.TryParse(parts[17], NumberStyles.HexNumber, null, out var or))
            OverallRank = or;

        // OverallBestLapTimeMs
        if (uint.TryParse(parts[18], NumberStyles.HexNumber, null, out var obl))
            OverallBestLapTimeMs = obl;

        // CurrentStatus
        bool isStatusDirty = false;
        if (CurrentStatus != parts[19])
        {
            isStatusDirty = true;
            CurrentStatus = parts[19];
        }

        // TrackStatus
        TrackStatus = parts[20].Trim();
        Flag = Heartbeat.ConvertTrackState(TrackStatus);

        // PitStopCount
        bool isPitStopCountDirty = false;
        if (ushort.TryParse(parts[21], NumberStyles.HexNumber, null, out var psc))
        {
            if (psc != PitStopCount)
            {
                isPitStopCountDirty = true;
                PitStopCount = psc;
            }
        }

        // LastLapPitted
        bool isLastLapPittedDirty = false;
        if (ushort.TryParse(parts[22], NumberStyles.HexNumber, null, out var llp))
        {
            if (llp != LastLapPitted)
            {
                isLastLapPittedDirty = true;
                LastLapPitted = llp;
            }
        }

        // StartPosition
        bool isStartPositionDirty = false;
        if (ushort.TryParse(parts[23], NumberStyles.HexNumber, null, out var sp))
        {
            if (sp != StartPosition)
            {
                isStartPositionDirty = true;
                StartPosition = sp;
            }
        }

        // LapsLed
        bool isLapsLedDirty = false;
        if (ushort.TryParse(parts[24], NumberStyles.HexNumber, null, out var ll))
        {
            if (ll != LapsLed)
            {
                isLapsLedDirty = true;
                LapsLed = ll;
            }
        }

        if (isStartPositionDirty || isLapsLedDirty || isStatusDirty)
        {
            changes.Add(new PositionInfoStateUpdate(this));
        }

        if (isLastLapPittedDirty || isPitStopCountDirty)
        {
            changes.Add(new PitStateUpdate(this));
        }

        return changes;
    }
}
