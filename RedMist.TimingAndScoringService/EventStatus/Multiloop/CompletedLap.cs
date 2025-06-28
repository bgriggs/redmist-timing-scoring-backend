using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

[Reactive]
public partial class CompletedLap : Message
{
    public partial ushort Rank { get; private set; }
    public partial string Number { get; private set; } = string.Empty;
    public partial uint UniqueIdentifier { get; private set; }
    public partial ushort CompletedLaps { get; private set; }
    public partial uint ElaspedTimeMs { get; private set; }
    public TimeSpan ElaspedTime => TimeSpan.FromMilliseconds(ElaspedTimeMs);
    public partial uint LastLapTimeMs { get; private set; }
    public TimeSpan LastLapTime => TimeSpan.FromMilliseconds(LastLapTimeMs);
    public partial string LapStatus { get; private set; } = string.Empty;
    public partial uint FastestLapTimeMs { get; private set; }
    public TimeSpan FastestLapTime => TimeSpan.FromMilliseconds(FastestLapTimeMs);
    public partial ushort FastestLap { get; private set; }
    public partial uint TimeBehindLeaderMs { get; private set; }
    public TimeSpan TimeBehindLeader => TimeSpan.FromMilliseconds(TimeBehindLeaderMs);
    public partial ushort LapsBehindLeader { get; private set; }
    public partial uint TimeBehindPrecedingCarMs { get; private set; }
    public TimeSpan TimeBehindPrecedingCar => TimeSpan.FromMilliseconds(TimeBehindPrecedingCarMs);
    public partial ushort LapsBehindPrecedingCar { get; private set; }
    public partial ushort OverallRank { get; private set; }
    public partial uint OverallBestLapTimeMs { get; private set; }
    public TimeSpan OverallBestLapTime => TimeSpan.FromMilliseconds(OverallBestLapTimeMs);
    public partial string CurrentStatus { get; private set; } = string.Empty;
    public partial string TrackStatus { get; private set; } = string.Empty;
    public Flags Flag { get; private set; }
    public partial ushort PitStopCount { get; private set; }
    public partial ushort LastLapPitted { get; private set; }
    public partial ushort StartPosition { get; private set; }
    public partial ushort LapsLed { get; private set; }


    public bool IsDirty { get; private set; }


    public CompletedLap()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$C�U�80004�Q1�C�0�8�4�83DDF�1CB83�T�1CB83�4�2E6A�0�649�0�C�1CB83�Unknown�G�1�0�9�0</example>
    public void ProcessC(string data)
    {
        var parts = ProcessHeader(data);

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

        // ElaspedTimeMs
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var et))
            ElaspedTimeMs = et;

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
        CurrentStatus = parts[19];

        // TrackStatus
        TrackStatus = parts[20].Trim();
        Flag = Heartbeat.ConvertTrackState(TrackStatus);

        // PitStopCount
        if (ushort.TryParse(parts[21], NumberStyles.HexNumber, null, out var psc))
            PitStopCount = psc;

        // LastLapPitted
        if (ushort.TryParse(parts[22], NumberStyles.HexNumber, null, out var llp))
            LastLapPitted = llp;

        // StartPosition
        if (ushort.TryParse(parts[23], NumberStyles.HexNumber, null, out var sp))
            StartPosition = sp;

        // LapsLed
        if (ushort.TryParse(parts[24], NumberStyles.HexNumber, null, out var ll))
            LapsLed = ll;
    }
}
