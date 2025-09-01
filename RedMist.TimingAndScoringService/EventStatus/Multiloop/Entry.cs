using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

public class Entry : Message
{
    public string Number { get; private set; } = string.Empty;
    public uint UniqueIdentifier { get; private set; }
    public string DriverName { get; private set; } = string.Empty;
    public ushort StartPosition { get; private set; }
    public byte FieldCount { get; private set; }
    public List<string> Fields { get; } = [];
    public uint CompetitorIdentifier { get; set; }


    public static string GetEntryNumber(string data)
    {
        var parts = data.Split(Consts.DELIM);
        if (parts.Length > 4)
            return parts[4].Trim();
        return string.Empty;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <example>$E�R�17�Q1�12�17�Steve Introne�18�B�B-Spec�Honda Fit�Windham NH�NER�180337�White�Sripath/PurposeEnergy/BlackHog Beer/BostonMobileTire/Hyperco/G-Loc Brakes/Introne Comm�����17�</example>
    public void ProcessE(string data)
    {
        var parts = ProcessHeader(data);

        // Number
        Number = parts[4].Trim();

        // UniqueIdentifier
        if (uint.TryParse(parts[5], NumberStyles.HexNumber, null, out var ui))
            UniqueIdentifier = ui;

        // DriverName
        DriverName = parts[6].Trim();

        // StartPosition
        if (ushort.TryParse(parts[7], NumberStyles.HexNumber, null, out var sp))
            StartPosition = sp;

        // FieldCount
        if (byte.TryParse(parts[8], NumberStyles.HexNumber, null, out var fc))
            FieldCount = fc;

        for (int i = 0; i < FieldCount; i++)
        {
            Fields.Add(parts[9 + i].Trim());
        }

        // CompetitorIdentifier
        if (uint.TryParse(parts[9 + FieldCount], NumberStyles.HexNumber, null, out var ci))
            CompetitorIdentifier = ci;
    }
}
