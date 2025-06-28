using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

public class Message
{
    public RecordType RecordType { get; set; }
    public uint Sequence { get; set; }
    public string Preamble { get; set; } = string.Empty;

    /// <summary>
    /// Update the first 3 parts after the command that make up the header.
    /// </summary>
    /// <example>$H�N�139B�Q1...</example>
    /// <returns>true: processing successful</returns>
    public string[] ProcessHeader(string data)
    {
        var parts = data.Split(Consts.DELIM);

        RecordType = parts[1].Trim() switch
        {
            "R" => RecordType.Repeated,
            "U" => RecordType.Updated,
            _ => RecordType.New,
        };

        Sequence = uint.Parse(parts[2], NumberStyles.HexNumber);
        Preamble = parts[3].Trim();
        return parts;
    }
}
