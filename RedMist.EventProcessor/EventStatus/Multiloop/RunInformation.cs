using System.Globalization;

namespace RedMist.EventProcessor.EventStatus.Multiloop;

[Reactive]
public partial class RunInformation : Message
{
    private readonly DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public partial string EventName { get; private set; } = string.Empty;
    public partial string EventShortName { get; private set; } = string.Empty;
    public partial string RunName { get; private set; } = string.Empty;
    public partial string RunTypeStr { get; private set; } = string.Empty;
    public RunType RunType => RunTypeStr switch
    {
        "P" => RunType.Practice,
        "Q" => RunType.Qualifying,
        "S" => RunType.SingleCarQualifying,
        _ => RunType.Race,
    };

    /// <summary>
    /// Time in seconds since 1/1/1970.
    /// </summary>
    public uint StartTimeDateSec { get; private set; }

    public DateTime StartTimeDate => epoch.AddSeconds(StartTimeDateSec);

    public bool IsDirty { get; private set; }


    public RunInformation()
    {
        PropertyChanged += (sender, args) => IsDirty = true;
    }


    public void ResetDirty() => IsDirty = false;

    /// <summary>
    /// 
    /// </summary>
    /// <example>$R�R�400004C7�Q1�Watkins Glen Hoosier Super Tour��Grp 2  FA FC FE2 P P2 Qual 1�Q�685ECBB8</example>
    public void ProcessR(string data)
    {
        var parts = ProcessHeader(data);

        // EventName
        EventName = parts[4].Trim();
        // EventShortName
        EventShortName = parts[5].Trim();
        // RunName
        RunName = parts[6].Trim();
        // RunType
        RunTypeStr = parts[7].Trim();
        // StartTimeDateSec
        if (uint.TryParse(parts[8], NumberStyles.HexNumber, null, out var std))
            StartTimeDateSec = std;
    }
}
