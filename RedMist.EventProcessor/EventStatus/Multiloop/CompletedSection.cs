using Riok.Mapperly.Abstractions;
using System.Globalization;

namespace RedMist.EventProcessor.EventStatus.Multiloop;

public class CompletedSection : Message
{
    public string Number { get; private set; } = string.Empty;
    [MapperIgnore]
    public uint UniqueIdentifier { get; private set; }
    public string SectionIdentifier { get; private set; } = string.Empty;
    public uint ElapsedTimeMs { get; private set; }
    [MapperIgnore]
    public TimeSpan ElapsedTime => TimeSpan.FromMilliseconds(ElapsedTimeMs);
    public uint LastSectionTimeMs { get; private set; }
    [MapperIgnore]
    public TimeSpan LastSectionTime => TimeSpan.FromMilliseconds(LastSectionTimeMs);
    public ushort LastLap { get; private set; }


    /// <summary>
    /// Gets the car number and section ID from a completed section message.
    /// </summary>
    /// <param name="data"></param>
    /// <returns>car number or empty if the data is malformed</returns>
    public static (string number, string section) GetNumberAndSection(string data)
    {
        var parts = data.Split(Consts.DELIM);
        if (parts.Length > 6)
            return (parts[4].Trim(), parts[6].Trim());
        return (string.Empty, string.Empty);
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

        // ElapsedTimeMs
        if (uint.TryParse(parts[7], NumberStyles.HexNumber, null, out var et))
            ElapsedTimeMs = et;

        // LastSectionTimeMs
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var lst))
            LastSectionTimeMs = lst;

        // LastLap
        if (ushort.TryParse(parts[9], NumberStyles.HexNumber, null, out var ll))
            LastLap = ll;
    }
}
