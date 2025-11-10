using System.Globalization;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace RedMist.EventProcessor.EventStatus.Multiloop;

[Reactive]
public partial class TrackInformation : Message
{
    public partial string Name { get; private set; } = string.Empty;
    public partial string Venue { get; private set; } = string.Empty;
    public partial string LengthMi { get; private set; } = string.Empty;
    public byte SectionCount { get; private set; }
    public List<Section> Sections { get; } = [];

    public bool IsDirty { get; private set; }


    public TrackInformation()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    /// <summary>
    /// 
    /// </summary>
    /// <example>$T�R�22�Q1�Watkins Glen�WGI�3.4�4�S1�0�SF�IM1�S2�0�IM1�IM3�S3�0�IM3�SF�Spd�1438�IM1�IM2</example>
    public void ProcessT(string data)
    {
        var parts = ProcessHeader(data);

        // Name
        Name = parts[4].Trim();
        // Venue
        Venue = parts[5].Trim();
        // LengthMi
        LengthMi = parts[6].Trim();

        // SectionCount
        if (byte.TryParse(parts[7], NumberStyles.HexNumber, null, out var sc))
            SectionCount = sc;

        int offset = 7;
        for (int i = 0; i < SectionCount; i++)
        {
            var sec = new Section
            {
                Name = parts[++offset].Trim(),
                LengthInches = parts[++offset].Trim(),
                StartLabel = parts[++offset].Trim(),
                EndLabel = parts[++offset].Trim()
            };
            Sections.Add(sec);
        }
    }
}
