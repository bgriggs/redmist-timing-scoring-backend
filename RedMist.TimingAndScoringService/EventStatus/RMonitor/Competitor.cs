using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

[Reactive]
public partial class Competitor
{
    [IgnoreReactive]
    public string RegistrationNumber { get; private set; } = string.Empty;
    public partial string Number { get; private set; } = string.Empty;
    public partial int Transponder { get; private set; }
    public partial string FirstName { get; private set; } = string.Empty;
    public partial string LastName { get; private set; } = string.Empty;
    public partial string Country { get; private set; } = string.Empty;
    public partial int ClassNumber { get; private set; }
    public partial string AdditionalData { get; private set; } = string.Empty;

    public bool IsDirty { get; private set; }

    public Competitor()
    {
        PropertyChanged += (sender, args) =>
        {
            IsDirty = true;
        };
    }


    public void ProcessA(string[] parts)
    {
        RegistrationNumber = parts[1].Replace("\"", "");
        Number = parts[2].Replace("\"", "");
        Transponder = int.Parse(parts[3]);
        FirstName = parts[4].Replace("\"", "");
        LastName = parts[5].Replace("\"", "");
        Country = parts[6].Replace("\"", "");
        ClassNumber = int.Parse(parts[7]);
    }

    public void ParseComp(string[] parts)
    {
        RegistrationNumber = parts[1].Replace("\"", "");
        Number = parts[2].Replace("\"", "");
        ClassNumber = int.Parse(parts[3]);
        FirstName = parts[4].Replace("\"", "");
        LastName = parts[5].Replace("\"", "");
        Country = parts[6].Replace("\"", "");
        AdditionalData = parts[7].Replace("\"", "");
    }

    public EventEntry ToEventEntry()
    {
        return ToEventEntry(_ => null);
    }

    public EventEntry ToEventEntry(Func<int, string?> getClassName)
    {
        var @class = getClassName(ClassNumber);
        
        return new EventEntry
        {
            Number = Number,
            Name = $"{FirstName} {LastName}".Trim(),
            Team = AdditionalData,
            Class = @class ?? ClassNumber.ToString()
        };
    }

    public EventEntry? ToEventEntryWhenDirtyWithReset(Func<int, string?> getClassName)
    {
        if (!IsDirty)
            return null;
        IsDirty = false;
        return ToEventEntry(getClassName);
    }
}
