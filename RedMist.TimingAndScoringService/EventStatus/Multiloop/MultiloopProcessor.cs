namespace RedMist.TimingAndScoringService.EventStatus.Multiloop;

/// <summary>
/// 
/// </summary>
/// <see cref="https://www.scribd.com/document/212233593/Multiloop-Timing-Protocol"/>
public class MultiloopProcessor
{
    private ILogger Logger { get; }
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Heartbeat Heartbeat { get; } = new Heartbeat();
    public Dictionary<string, Entry> Entries { get; } = [];
    public Dictionary<string, CompletedLap> CompletedLaps { get; } = [];
    public Dictionary<string, CompletedSection> CompletedSections { get; } = [];
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


    public async Task Process(string data, CancellationToken stoppingToken = default)
    {
        var commands = data.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await _lock.WaitAsync(stoppingToken);
        try
        {
            foreach (var command in commands)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;
                try
                {
                    ProcessCommand(command);
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
    }

    private void ProcessCommand(string data)
    {
        if (data.StartsWith("$H"))
        {
            Heartbeat.ProcessH(data);
        }
        else if (data.StartsWith("$E"))
        {
            var entry = new Entry();
            entry.ProcessE(data);
            if (!string.IsNullOrWhiteSpace(entry.Number))
            {
                Entries[entry.Number] = entry;
            }
        }
        else if (data.StartsWith("$C"))
        {
            var cl = new CompletedLap();
            cl.ProcessC(data);
            if (!string.IsNullOrWhiteSpace(cl.Number))
            {
                CompletedLaps[cl.Number] = cl;
            }
        }
        else if (data.StartsWith("$S"))
        {
            var s = new CompletedSection();
            s.ProcessS(data);
            if (!string.IsNullOrWhiteSpace(s.Number))
            {
                CompletedSections[s.Number] = s;
            }
        }
        else if (data.StartsWith("$L"))
        {
            var l = new LineCrossing();
            l.ProcessL(data);
            if (!string.IsNullOrWhiteSpace(l.Number))
            {
                LineCrossings[l.Number] = l;
            }
        }
        else if (data.StartsWith("$I"))
        {
            var i = new InvalidatedLap();
            i.ProcessI(data);
        }
        else if (data.StartsWith("$F"))
        {
            FlagInformation.ProcessF(data);
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
        }
        else if (data.StartsWith("$V"))
        {
            Version.ProcessV(data);
        }
    }
}
