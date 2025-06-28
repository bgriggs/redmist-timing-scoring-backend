using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

[Reactive]
public partial class InvalidatedLap : Message
{
    public partial string Number { get; private set; } = string.Empty;
    public partial uint UniqueIdentifier { get; private set; }
    public partial uint ElaspedTimeMs { get; private set; }
    public TimeSpan ElaspedTime => TimeSpan.FromMilliseconds(ElaspedTimeMs);

    public bool IsDirty { get; private set; }


    public InvalidatedLap()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$I�U�F005A�Q1�??�F005A�0�</example>
    public void ProcessI(string data)
    {
        var parts = ProcessHeader(data);

        // Number
        Number = parts[4].Trim();

        // UniqueIdentifier
        if (uint.TryParse(parts[5], NumberStyles.HexNumber, null, out var ui))
            UniqueIdentifier = ui;

        // ElaspedTimeMs
        if (uint.TryParse(parts[6], NumberStyles.HexNumber, null, out var et))
            ElaspedTimeMs = et;
    }
}
