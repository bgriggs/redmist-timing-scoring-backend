using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

[Reactive]
public partial class CompletedSection : Message
{
    public partial string Number { get; private set; } = string.Empty;
    public partial uint UniqueIdentifier { get; private set; }
    public partial string SectionIdentifier { get; private set; } = string.Empty;
    public partial uint ElaspedTimeMs { get; private set; }
    public TimeSpan ElaspedTime => TimeSpan.FromMilliseconds(ElaspedTimeMs);
    public partial uint LastSectionTimeMs { get; private set; }
    public TimeSpan LastSectionTime => TimeSpan.FromMilliseconds(LastSectionTimeMs);
    public partial ushort LastLap { get; private set; }

    public bool IsDirty { get; private set; }


    public CompletedSection()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$S�N�F3170000�Q1�99�EF317�S1�2DF3C0E�7C07�5</example>
    public void ProcessS(string data)
    {
        var parts = ProcessHeader(data);
        // Number
        Number = parts[4].Trim();

        // UniqueIdentifier
        if (uint.TryParse(parts[5], NumberStyles.HexNumber, null, out var ui))
            UniqueIdentifier = ui;

        // SectionIdentifier
        SectionIdentifier = parts[6].Trim();

        // ElaspedTimeMs
        if (uint.TryParse(parts[7], NumberStyles.HexNumber, null, out var et))
            ElaspedTimeMs = et;

        // LastSectionTimeMs
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var lst))
            LastSectionTimeMs = lst;

        // LastLap
        if (ushort.TryParse(parts[9], NumberStyles.HexNumber, null, out var ll))
            LastLap = ll;
    }
}
