using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

/// <summary>
/// Result Monitor data format processor such as from an Orbits timing system.
/// </summary>
/// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
public class RMonitorDataProcessorV2
{
    private ILogger Logger { get; }
    private readonly SemaphoreSlim _lock = new(1, 1);
    public Heartbeat Heartbeat { get; } = new();
    private readonly Dictionary<int, string> classes = [];
    private readonly Dictionary<string, Competitor> competitors = [];
    private readonly Dictionary<string, RaceInformation> raceInformation = [];
    //private readonly Dictionary<string, RaceInformation> startingPositions = [];
    //private readonly Dictionary<string, int> inClassStartingPositions = [];
    private readonly Dictionary<string, PracticeQualifying> practiceQualifying = [];
    private readonly Dictionary<string, PassingInformation> passingInformation = [];
    public int SessionReference { get; set; }
    public string SessionName { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public double TrackLength { get; set; }


    public RMonitorDataProcessorV2(ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task<SessionStateUpdate?> Process(TimingMessage message)
    {
        if (message.Type != "rmonitor")
            return null;

        var changes = new List<ISessionStateChange>();

        await _lock.WaitAsync();
        try
        {
            bool competitorChanged = false;
            var commands = message.Data.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var command in commands)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                try
                {
                    if (command.StartsWith("$F"))
                    {
                        // Heartbeat message
                        var ch = ProcessF(command);
                        changes.Add(ch);
                    }
                    else if (command.StartsWith("$A"))
                    {
                        // Competitor information
                        ProcessA(command);
                        competitorChanged = true;
                    }
                    else if (command.StartsWith("$COMP"))
                    {
                        // Competitor information
                        ProcessComp(command);
                        competitorChanged = true;
                    }
                    else if (command.StartsWith("$B"))
                    {
                        // Session/run information
                        var ch = ProcessB(command);
                        if (ch != null)
                            changes.Add(ch);
                    }
                    else if (command.StartsWith("$C"))
                    {
                        // Class information
                        ProcessC(command);
                        competitorChanged = true;
                    }
                    else if (command.StartsWith("$E"))
                    {
                        // Setting (track) information
                        ProcessE(command);
                    }
                    else if (command.StartsWith("$G"))
                    {
                        // Race information
                        var ch = ProcessG(command);
                        if (ch != null)
                            changes.Add(ch);
                    }
                    else if (command.StartsWith("$H"))
                    {
                        // Practice/qualifying information
                        var ch = ProcessH(command);
                        if (ch != null)
                            changes.Add(ch);
                    }
                    else if (command.StartsWith("$I"))
                    {
                        // Init record (reset)
                        ProcessI();
                    }
                    else if (command.StartsWith("$J"))
                    {
                        // Passing information
                        var ch = ProcessJ(command);
                        if (ch != null)
                            changes.Add(ch);
                    }
                    else if (command.StartsWith("$COR"))
                    {
                        // Corrected Finish Time
                        ProcessCor(command);
                    }
                    else
                    {
                        Logger.LogWarning("Unknown command: {cmd}", command);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing command: {cmd}", command);
                }
            }

            if (competitorChanged)
            {
                changes.Add(new CompetitorStateUpdate([.. competitors.Values], classes.ToDictionary()));
            }
        }
        finally
        {
            _lock.Release();
        }

        return new SessionStateUpdate("rmonitor", changes);
    }

    #region Result Monitor
    #region Heartbeat message

    /// <summary>
    /// Processes $F –- Heartbeat message
    /// </summary>
    /// <example>$F,14,"00:12:45","13:34:23","00:09:47","Green "</example>
    private ISessionStateChange ProcessF(string data)
    {
        Heartbeat.ProcessF(data);
        return new HeartbeatStateUpdate(Heartbeat);
    }

    #endregion

    #region Competitors

    /// <summary>
    /// Processes $A messages -- Competitor information
    /// </summary>
    /// <example>$A,"1234BE","12X",52474,"John","Johnson","USA",5</example>
    private void ProcessA(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[1].Replace("\"", "");
        if (!competitors.TryGetValue(regNum, out var competitor))
        {
            competitor = new Competitor();
            competitors[regNum] = competitor;
        }
        competitor.ProcessA(parts);
    }

    /// <summary>
    /// Processes $COMP –- Competitor information
    /// </summary>
    /// <example>$COMP,"1234BE","12X",5,"John","Johnson","USA","CAMEL"</example>
    private void ProcessComp(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[1].Replace("\"", "");
        if (!competitors.TryGetValue(regNum, out var competitor))
        {
            competitor = new Competitor();
            competitors[regNum] = competitor;
        }
        competitor.ParseComp(parts);
    }

    #endregion

    #region Session information

    /// <summary>
    /// Processes $B messages -- Run Information.
    /// </summary>
    /// <example>$B,5,"Friday free practice"</example>
    private ISessionStateChange? ProcessB(string data)
    {
        var parts = data.Split(',');
        var sr = SessionReference;
        SessionReference = int.Parse(parts[1]);
        SessionName = parts[2].Replace("\"", "");
        if (sr != SessionReference)
        {
            return new SessionStateUpdated(SessionReference, SessionName);
        }
        return null;
    }

    #endregion

    #region Classes

    /// <summary>
    /// Processes $C messages.
    /// </summary>
    /// <example>$C,5,"Formula 300"</example>
    private void ProcessC(string data)
    {
        var parts = data.Split(',');
        var num = int.Parse(parts[1]);
        var className = parts[2].Replace("\"", "").Trim();
        classes[num] = className;
    }

    #endregion

    #region Setting information

    /// <summary>
    /// Processes $E messages.
    /// </summary>
    /// <example>$E,"TRACKNAME","Indianapolis Motor Speedway"</example>
    private void ProcessE(string data)
    {
        var parts = data.Split(',');
        var desc = parts[1].Replace("\"", "");
        if (desc == "TRACKNAME")
        {
            TrackName = parts[2].Replace("\"", "");
        }
        else if (desc == "TRACKLENGTH")
        {
            // Track length
            TrackLength = double.Parse(parts[2].Replace("\"", ""));
        }
    }

    #endregion

    #region Race information

    /// <summary>
    /// Processes $G messages.
    /// </summary>
    /// <example>$G,3,"1234BE",14,"01:12:47.872"</example>
    /// <example>$G,10,"89",,"00:00:00.000"</example>
    private ISessionStateChange? ProcessG(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[2].Replace("\"", "");
        if (!raceInformation.TryGetValue(regNum, out var raceInfo))
        {
            raceInfo = new RaceInformation();
            raceInformation[regNum] = raceInfo;
        }
        return raceInfo.ProcessG(parts);
       

        //TODO: infer starting positions
        //// Save off starting positions
        //if (raceInfo.Laps == 0 && !RaceHasPassedStart())
        //{
        //    var flag = Heartbeat.FlagStatus.ToFlag();
        //    UpdateStartingPosition(parts, regNum, flag);
        //}
    }

    //private void UpdateStartingPosition(string[] parts, string regNum, Flags flag)
    //{
    //    // Allow capture of starting positions during lap 0 up to and including the green flag
    //    if (flag == Flags.Unknown || flag == Flags.Yellow || flag == Flags.Green)
    //    {
    //        // Make a copy for storing off
    //        var sp = new RaceInformation();
    //        sp.ProcessG(parts);
    //        startingPositions[regNum] = sp;
    //        UpdateInClassStartingPositionLookup();
    //    }
    //}

    //private void UpdateInClassStartingPositionLookup()
    //{
    //    var entries = new List<(string num, int @class, int pos)>();
    //    foreach (var regNum in startingPositions.Keys)
    //    {
    //        var ri = startingPositions[regNum];
    //        if (!competitors.TryGetValue(regNum, out var comp))
    //        {
    //            Logger.LogWarning("Competitor {rn} not found for starting position", regNum);
    //            continue;
    //        }
    //        entries.Add((regNum, comp.ClassNumber, ri.Position));
    //    }

    //    var classGroups = entries.GroupBy(x => x.@class);
    //    foreach (var classGroup in classGroups)
    //    {
    //        var positions = classGroup.OrderBy(x => x.pos).ToList();
    //        for (int i = 0; i < positions.Count; i++)
    //        {
    //            var entry = positions[i];
    //            inClassStartingPositions[entry.num] = i + 1;
    //        }
    //    }
    //}

    #endregion

    #region Practice/qualifying information

    /// <summary>
    /// Processes $H messages.
    /// </summary>
    /// <example>$H,2,"1234BE",3,"00:02:17.872"</example>
    private ISessionStateChange? ProcessH(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[2].Replace("\"", "");
        if (!practiceQualifying.TryGetValue(regNum, out var pq))
        {
            pq = new PracticeQualifying();
            practiceQualifying[regNum] = pq;
        }
        return pq.ProcessH(parts);
    }

    #endregion

    #region Init record

    /// <summary>
    /// Processes $I messages.
    /// </summary>
    /// <example>$I,"16:36:08.000","12 jan 01"</example>
    private void ProcessI()
    {
        // Adhere to reset to maintain consistency. This is required for occurrences such as mid-race car removals
        competitors.Clear();
        raceInformation.Clear();
        practiceQualifying.Clear();
        //secondaryProcessor.Clear();

        // Allow for reset when the event is initializing. Once it has started,
        // suppress the resets to reduce user confusion
        var flag = Heartbeat.FlagStatus.ToFlag();
        if (flag == Flags.Unknown)
        {
            classes.Clear();
            passingInformation.Clear();
            //startingPositions.Clear();
            //inClassStartingPositions.Clear();
        }
        //PublishEventReset();
    }

    #endregion

    #region Passing information

    /// <summary>
    /// Processes $J messages.
    /// </summary>
    /// <example>$J,"1234BE","00:02:03.826","01:42:17.672"</example>
    private ISessionStateChange? ProcessJ(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[1].Replace("\"", "");
        if (!passingInformation.TryGetValue(regNum, out var pq))
        {
            pq = new PassingInformation();
            passingInformation[regNum] = pq;
        }
        return pq.ProcessJ(parts);
    }

    #endregion

    #region Corrected Finish Time

    /// <summary>
    /// Processes $COR messages.
    /// </summary>
    /// <example>$COR,"123BE","658",2,"00:00:35.272","+00:00:00.012"</example>
    private void ProcessCor(string data) { }

    #endregion
    #endregion
}
