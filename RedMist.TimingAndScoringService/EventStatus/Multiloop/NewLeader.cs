using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

[Reactive]
public partial class NewLeader : Message
{
    public partial string Number { get; private set; } = string.Empty;
    public partial uint UniqueIdentifier { get; private set; }
    public partial ushort LapNumber { get; private set; }
    public partial uint ElaspedTimeMs { get; private set; }
    public TimeSpan ElaspedTime => TimeSpan.FromMilliseconds(ElaspedTimeMs);
    public partial ushort LeadChangeIndex { get; private set; }


    public bool IsDirty { get; private set; }


    public NewLeader()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$N�U�80004�Q1�01�469D�45�4CAD63�20</example>
    public void ProcessC(string data)
    {
        var parts = ProcessHeader(data);

        // Number
        Number = parts[4];

        // UniqueIdentifier
        if (uint.TryParse(parts[5], NumberStyles.HexNumber, null, out var ui))
            UniqueIdentifier = ui;

        // LapNumber
        if (ushort.TryParse(parts[6], NumberStyles.HexNumber, null, out var ln))
            LapNumber = ln;

        // ElaspedTimeMs
        if (uint.TryParse(parts[7], NumberStyles.HexNumber, null, out var et))
            ElaspedTimeMs = et;

        // LeadChangeIndex
        if (ushort.TryParse(parts[8], NumberStyles.HexNumber, null, out var lci))
            LeadChangeIndex = lci;
    }
}
