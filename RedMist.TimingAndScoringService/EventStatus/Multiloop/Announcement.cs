using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

[Reactive]
public partial class Announcement : Message
{
    private readonly DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public partial ushort MessageNumber { get; private set; }
    public partial string ActionStr { get; private set; } = string.Empty;
    public partial string PriorityStr { get; private set; } = string.Empty;

    /// <summary>
    /// Time in seconds since 1/1/1970.
    /// </summary>
    public uint TimestampSecs { get; private set; }

    public DateTime Timestamp => epoch.AddSeconds(TimestampSecs);
    public partial string Text { get; private set; } = string.Empty;

    public bool IsDirty { get; private set; }


    public Announcement()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$A�N�F3170000�Q1�2F�A�U�BC6AD080�Some Message</example>
    public void ProcessA(string data)
    {
        var parts = ProcessHeader(data);

        // MessageNumber
        if (ushort.TryParse(parts[4], NumberStyles.HexNumber, null, out var m))
            MessageNumber = m;

        // ActionStr
        ActionStr = parts[5].Trim();
        // PriorityStr
        PriorityStr = parts[6].Trim();
        // TimestampSecs
        if (uint.TryParse(parts[7], NumberStyles.HexNumber, null, out var t))
            TimestampSecs = t;
        Text = parts[8].Trim();
    }
}
