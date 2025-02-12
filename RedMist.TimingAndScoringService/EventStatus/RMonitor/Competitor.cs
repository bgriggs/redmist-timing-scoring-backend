using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

public class Competitor
{
    public string RegistrationNumber { get; private set; } = string.Empty;
    public string Number { get; private set; } = string.Empty;
    public int Transponder { get; private set; }
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public string Country { get; private set; } = string.Empty;
    public int ClassNumber { get; private set; }
    public string AdditionalData { get; private set; } = string.Empty;

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
        return new EventEntry
        {
            Number = Number,
            Name = $"{FirstName} {LastName}".Trim(),
            Team = AdditionalData,
            Class = ClassNumber.ToString()
        };
    }
}
