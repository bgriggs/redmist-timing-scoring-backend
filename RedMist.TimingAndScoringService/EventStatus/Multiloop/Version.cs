using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

[Reactive]
public partial class Version : Message
{
    public partial ushort Major { get; private set; }
    public partial ushort Minor { get; private set; }
    public partial string Info { get; private set; } = string.Empty;

    public bool IsDirty { get; private set; }


    public Version()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$V�R�1�Q1�1�5�Multiloop feed</example>
    public void ProcessV(string data)
    {
        var parts = ProcessHeader(data);

        // Major
        if (ushort.TryParse(parts[4], NumberStyles.HexNumber, null, out var mj))
            Major = mj;
        // Minor
        if (ushort.TryParse(parts[5], NumberStyles.HexNumber, null, out var mi))
            Minor = mi;
        // Info
        Info = parts[6];
    }
}
