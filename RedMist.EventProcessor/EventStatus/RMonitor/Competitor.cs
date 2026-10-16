using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.RMonitor;

[Reactive]
public partial class Competitor
{
    [IgnoreReactive]
    public string RegistrationNumber { get; private set; } = string.Empty;
    public partial string Number { get; private set; } = string.Empty;
    public partial uint Transponder { get; private set; }
    public partial string FirstName { get; private set; } = string.Empty;
    public partial string LastName { get; private set; } = string.Empty;
    public partial string Country { get; private set; } = string.Empty;
    public partial int ClassNumber { get; private set; }
    public partial string AdditionalData { get; private set; } = string.Empty;

    public bool IsDirty { get; private set; }

    public Competitor()
    {
        PropertyChanged += (sender, args) => IsDirty = true;
    }


    public void ProcessA(string[] parts)
    {
        RegistrationNumber = parts[1].Replace("\"", "");
        Number = parts[2].Replace("\"", "");
        if (uint.TryParse(parts[3], out uint transponder))
            Transponder = transponder;
        // A blank transponder is cleared, not ignored: the field emptied in the timing system means the
        // car no longer has one, and keeping the old value would still tie it to that transponder.
        else if (string.IsNullOrWhiteSpace(parts[3].Replace("\"", "")))
            Transponder = 0;
        FirstName = parts[4].Replace("\"", "");
        LastName = parts[5].Replace("\"", "");
        Country = parts[6].Replace("\"", "");
        if (int.TryParse(parts[7], out int c))
            ClassNumber = c;
    }

    public void ParseComp(string[] parts)
    {
        RegistrationNumber = parts[1].Replace("\"", "");
        Number = parts[2].Replace("\"", "");
        if (int.TryParse(parts[3], out int c))
            ClassNumber = c;
        FirstName = parts[4].Replace("\"", "");
        LastName = parts[5].Replace("\"", "");
        Country = parts[6].Replace("\"", "");
        AdditionalData = parts[7].Replace("\"", "").Trim();
    }

    /// <summary>
    /// Carries the team name over from the car's record under its previous number, for a renumber
    /// where the timing system sent the new number's $A without a $COMP.
    /// </summary>
    public void InheritAdditionalData(Competitor previous)
    {
        if (string.IsNullOrEmpty(AdditionalData))
            AdditionalData = previous.AdditionalData;
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
