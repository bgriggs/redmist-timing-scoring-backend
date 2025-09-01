using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

/// <summary>
/// Responsible to decode and maintain the state of a Multiloop timing system.
/// </summary>
/// <see cref="https://www.scribd.com/document/212233593/Multiloop-Timing-Protocol"/>
public class MultiloopProcessor
{
    private ILogger Logger { get; }
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Heartbeat Heartbeat { get; } = new Heartbeat();
    public Dictionary<string, Entry> Entries { get; } = [];
    public Dictionary<string, CompletedLap> CompletedLaps { get; } = [];
    /// <summary>
    /// Key1: Car number, Key2: Section identifier
    /// </summary>
    public Dictionary<string, Dictionary<string, CompletedSection>> CompletedSections { get; } = [];
    public Dictionary<string, LineCrossing> LineCrossings { get; } = [];
    public FlagInformation FlagInformation { get; } = new FlagInformation();
    public NewLeader NewLeader { get; } = new NewLeader();
    public RunInformation RunInformation { get; } = new RunInformation();
    public TrackInformation TrackInformation { get; } = new TrackInformation();
    public Dictionary<ushort, Announcement> Announcements { get; } = [];
    public Version Version { get; } = new Version();


    public MultiloopProcessor(ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task<SessionStateUpdate?> Process(TimingMessage message, CancellationToken stoppingToken = default)
    {
        if (message.Type != "multiloop")
            return null;

        var changes = new List<ISessionStateChange>();
        
        var commands = message.Data.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await _lock.WaitAsync(stoppingToken);
        try
        {
            foreach (var command in commands)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;
                try
                {
                    var updates = ProcessCommand(command);
                    changes.AddRange(updates);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing multiloop command: {cmd}", command);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
        return new SessionStateUpdate("multiloop", changes);
    }

    private List<ISessionStateChange> ProcessCommand(string data)
    {
        var changes = new List<ISessionStateChange>();
        if (data.StartsWith("$H"))
        {
            Heartbeat.ProcessH(data);
        }
        else if (data.StartsWith("$E"))
        {
            var num = Entry.GetEntryNumber(data);
            if (string.IsNullOrEmpty(num))
            {
                Logger.LogWarning("Entry message received with no car number: {data}", data);
                return changes;
            }
            if (!Entries.TryGetValue(num, out var entry))
            {
                entry = new Entry();
                Entries[num] = entry;
            }
            entry.ProcessE(data);
        }
        else if (data.StartsWith("$C"))
        {
            var num = CompletedLap.GetNumber(data);
            if (string.IsNullOrEmpty(num))
            {
                Logger.LogWarning("Completed lap message received with no car number: {data}", data);
                return changes;
            }
            if (!CompletedLaps.TryGetValue(num, out var cl))
            {
                cl = new CompletedLap();
                CompletedLaps[num] = cl;
            }
            var chs = cl.ProcessC(data);
            changes.AddRange(chs);

            // Reset sections for the car as they have completed a lap
            if (CompletedSections.TryGetValue(num, out var sections))
            {
                sections.Clear();
                var update = new SectionStateUpdate(num, [.. sections.Values]);
                changes.Add(update);
            }
        }
        else if (data.StartsWith("$S"))
        {
            var (num, section) = CompletedSection.GetNumberAndSection(data);
            if (string.IsNullOrEmpty(num) || string.IsNullOrEmpty(section))
            {
                Logger.LogWarning("Completed section message received with no car number or section: {data}", data);
                return changes;
            }
            if (!CompletedSections.TryGetValue(num, out var sections))
            {
                sections = [];
                CompletedSections[num] = sections;
            }
            if (!sections.TryGetValue(section, out var cs))
            {
                cs = new CompletedSection();
                sections[section] = cs;
            }
            cs.ProcessS(data);
            var update = new SectionStateUpdate(num, [.. sections.Values]);
            changes.Add(update);
        }
        else if (data.StartsWith("$L"))
        {
            var num = LineCrossing.GetNumber(data);
            if (string.IsNullOrEmpty(num))
            {
                Logger.LogWarning("Line crossing message received with no car number: {data}", data);
                return changes;
            }
            if (!LineCrossings.TryGetValue(num, out var lc))
            {
                lc = new LineCrossing();
                LineCrossings[num] = lc;
            }
            var chs = lc.ProcessL(data);
            changes.AddRange(chs);
        }
        else if (data.StartsWith("$I"))
        {
            var i = new InvalidatedLap();
            i.ProcessI(data);
        }
        else if (data.StartsWith("$F"))
        {
            FlagInformation.ProcessF(data);
            if (FlagInformation.IsDirty)
            {
                changes.Add(new FlagMetricsStateUpdate(FlagInformation));
                FlagInformation.ResetDirty();
            }
        }
        else if (data.StartsWith("$N"))
        {
            NewLeader.ProcessN(data);
        }
        else if (data.StartsWith("$R"))
        {
            RunInformation.ProcessR(data);
        }
        else if (data.StartsWith("$T"))
        {
            TrackInformation.ProcessT(data);
        }
        else if (data.StartsWith("$A"))
        {
            var a = new Announcement();
            a.ProcessA(data);
            Announcements[a.MessageNumber] = a;
            var update = new AnnouncementStateUpdate(Announcements);
            changes.Add(update);
        }
        else if (data.StartsWith("$V"))
        {
            Version.ProcessV(data);
        }

        return changes;
    }
}
