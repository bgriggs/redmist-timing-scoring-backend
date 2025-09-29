using RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;
using RedMist.TimingAndScoringService.EventStatus.PositionEnricher;
using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

/// <summary>
/// Result Monitor data format processor such as from an Orbits timing system.
/// </summary>
/// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
public class RMonitorDataProcessorV2
{
    private ILogger Logger { get; }
    public Heartbeat Heartbeat { get; } = new();
    private readonly Dictionary<int, string> classes = [];
    private readonly Dictionary<string, Competitor> competitors = [];
    private readonly Dictionary<string, RaceInformation> raceInformation = [];
    private readonly Dictionary<string, PracticeQualifying> practiceQualifying = [];
    private readonly Dictionary<string, PassingInformation> passingInformation = [];
    public int SessionReference { get; set; }
    public string SessionName { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public double TrackLength { get; set; }
    private readonly SessionContext sessionContext;
    private readonly ResetProcessor resetProcessor;
    private readonly StartingPositionProcessor startingPositionProcessor;
    private const string STANDALONE_RESET_CMD = "$I, \"00:00:00\", \"0/0/0000\"";


    public RMonitorDataProcessorV2(ILoggerFactory loggerFactory, SessionContext sessionContext,
        ResetProcessor resetProcessor, StartingPositionProcessor startingPositionProcessor)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
        this.resetProcessor = resetProcessor;
        this.startingPositionProcessor = startingPositionProcessor;
    }


    /// <summary>
    /// Processes timing messages with immediate application of each command.
    /// </summary>
    /// <param name="message">The timing message to process</param>
    /// <param name="sessionContext">The session context to apply changes to immediately</param>
    /// <returns>List of all changes that were applied</returns>
    public async Task<PatchUpdates?> ProcessAsync(TimingMessage message, SessionContext sessionContext)
    {
        if (message.Type != Backend.Shared.Consts.RMONITOR_TYPE)
            return null;

        var sessionPatches = new List<SessionStatePatch>();
        var carPatches = new List<CarPositionPatch>();
        bool competitorChanged = false;

        var commands = message.Data.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool isMidRaceReset = false, isMidRaceStandaloneReset = false;
        if (commands.Length > 4)
            isMidRaceReset = IsMidRaceReset(message.Data);
        else if (commands.Length == 1)
            isMidRaceStandaloneReset = IsStandaloneMidRaceReset(message.Data);

        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command))
                continue;

            try
            {
                if (command.StartsWith("$F"))
                {
                    // Heartbeat message - apply immediately
                    var change = ProcessF(command);
                    var sp = change.ApplySessionChange(sessionContext.SessionState);
                    if (sp != null)
                        sessionPatches.Add(sp);
                }
                else if (command.StartsWith("$A"))
                {
                    // Competitor information
                    var regNum = ProcessA(command);
                    var p = AddUpdateCompetitor(regNum);
                    if (p != null)
                        carPatches.Add(p);
                    competitorChanged = true;
                }
                else if (command.StartsWith("$COMP"))
                {
                    // Competitor information
                    var regNum = ProcessComp(command);
                    var p = AddUpdateCompetitor(regNum);
                    if (p != null)
                        carPatches.Add(p);
                    competitorChanged = true;
                }
                else if (command.StartsWith("$B"))
                {
                    // Session/run information - apply immediately
                    var change = ProcessB(command);
                    if (change != null)
                    {
                        var sp = change.ApplySessionChange(sessionContext.SessionState);
                        if (sp != null)
                            sessionPatches.Add(sp);
                    }
                }
                else if (command.StartsWith("$C"))
                {
                    // Class information - accumulate these
                    ProcessC(command);
                    competitorChanged = true;
                }
                else if (command.StartsWith("$E"))
                {
                    // Setting (track) information - no state change
                    ProcessE(command);
                }
                else if (command.StartsWith("$G"))
                {
                    // Race information - apply immediately
                    var change = ProcessG(command);
                    if (change != null)
                    {
                        var cp = change.ApplyCarChange(sessionContext);
                        if (cp != null)
                            carPatches.Add(cp);
                    }
                }
                else if (command.StartsWith("$H"))
                {
                    // Practice/qualifying information - apply immediately
                    var change = ProcessH(command);
                    if (change != null)
                    {
                        var cp = change.ApplyCarChange(sessionContext);
                        if (cp != null)
                            carPatches.Add(cp);
                    }
                }
                else if (command.StartsWith("$I"))
                {
                    if (isMidRaceStandaloneReset)
                    {
                        // Ignore standalone reset commands that are not part of a reset sequence since 
                        // they create inconsistencies in the session state.
                        // If this causes issues, consider sending a relay reset command with a force reconnect
                        // to the timing system to make it send a full reset sequence.
                        Logger.LogInformation("Received standalone RESET command mid-race--IGNORING");
                        continue;
                    }

                    // Init record (reset) - apply immediately
                    ProcessI();

                    // Handle reset immediately as subsequent commands are likely grouped together in one string
                    await resetProcessor.Process();
                }
                else if (command.StartsWith("$J"))
                {
                    // Passing information - apply immediately
                    var change = ProcessJ(command);
                    if (change != null)
                    {
                        var cp = change.ApplyCarChange(sessionContext);
                        if (cp != null)
                            carPatches.Add(cp);
                    }
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

        // Apply accumulated competitor changes at the end
        if (competitorChanged)
        {
            //var cps = GetCarPatches(sessionContext);
            //carPatches.AddRange(cps);
        }

        // If this is a mid-race reset, have all cars re-evaluated for previous lap time 
        // since the reset does not resend them and it would otherwise take a lap for each car
        // to have their last lap time updated
        if (isMidRaceReset)
        {
            sessionContext.SetLastLapTimeBeforeReset();
            isMidRaceReset = false;
        }

        return new PatchUpdates([.. sessionPatches], [.. carPatches]);
    }

    private CarPositionPatch? AddUpdateCompetitor(string regNum)
    {
        if (competitors.TryGetValue(regNum, out var comp))
        {
            var patch = new CarPositionPatch();
            classes.TryGetValue(comp.ClassNumber, out var className);
            var car = sessionContext.GetCarByNumber(regNum);
            car ??= new CarPosition { Number = comp.Number };
            if (car.TransponderId != comp.Transponder)
            {
                car.TransponderId = comp.Transponder;
                patch.TransponderId = comp.Transponder;
            }
            if (car.DriverName != comp.FirstName)
            {
                car.DriverName = comp.FirstName;
                patch.DriverName = comp.FirstName;
            }
            if (car.Class != className)
            {
                car.Class = className;
                patch.Class = className;
            }
            var eid = sessionContext.SessionState.EventId.ToString();
            if (car.EventId != eid)
            {
                car.EventId = eid;
                patch.EventId = eid;
            }
            var sid = sessionContext.SessionState.SessionId.ToString();
            if (car.SessionId != sid)
            {
                car.SessionId = sid;
                patch.SessionId = sid;
            }
            if (car.TrackFlag != sessionContext.SessionState.CurrentFlag)
            {
                car.TrackFlag = sessionContext.SessionState.CurrentFlag;
                patch.TrackFlag = sessionContext.SessionState.CurrentFlag;
            }
            sessionContext.UpdateCars([car]);

            // Update event entry
            var entry = comp.ToEventEntry(GetClassName);
            sessionContext.SessionState.EventEntries.RemoveAll(e => e.Number == entry.Number);
            sessionContext.SessionState.EventEntries.Add(entry);

            if (TimingCommon.Models.Mappers.CarPositionMapper.IsValidPatch(patch))
            {
                patch.Number = car.Number; // Ensure number is set
                return patch;
            }
        }

        return null;
    }

    private string? GetClassName(int classId)
    {
        if (classes.TryGetValue(classId, out var className))
        {
            return className;
        }
        return null;
    }

    /// <summary>
    /// Determine if this is part of a mid-race reset sequence.
    /// </summary>
    /// <returns>true is a reset sequence</returns>
    private bool IsMidRaceReset(string data)
    {
        if (Heartbeat.FlagStatus == string.Empty)
            return false;
        // A multi-line command that includes at last the following should follow a reset
        return data.Contains("$I") && data.Contains("$A") && data.Contains("$COMP") && data.Contains("$G") && data.Contains("$H");
    }

    /// <summary>
    /// Determine if this is a standalone mid-race reset command.
    /// </summary>
    /// <returns>true is a standalone reset</returns>
    private bool IsStandaloneMidRaceReset(string data)
    {
        if (Heartbeat.FlagStatus == string.Empty)
            return false;
        // A single line command that is just the reset command
        return data.Trim() == STANDALONE_RESET_CMD;
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
    private string ProcessA(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[1].Replace("\"", "");
        if (!competitors.TryGetValue(regNum, out var competitor))
        {
            competitor = new Competitor();
            competitors[regNum] = competitor;
        }
        competitor.ProcessA(parts);
        return regNum;
    }

    /// <summary>
    /// Processes $COMP –- Competitor information
    /// </summary>
    /// <example>$COMP,"1234BE","12X",5,"John","Johnson","USA","CAMEL"</example>
    private string ProcessComp(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[1].Replace("\"", "");
        if (!competitors.TryGetValue(regNum, out var competitor))
        {
            competitor = new Competitor();
            competitors[regNum] = competitor;
        }
        competitor.ParseComp(parts);
        return regNum;
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
            return new StateChanges.SessionStateUpdate(SessionReference, SessionName);
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
    private ICarStateChange? ProcessG(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[2].Replace("\"", "");
        if (!raceInformation.TryGetValue(regNum, out var raceInfo))
        {
            raceInfo = new RaceInformation();
            raceInformation[regNum] = raceInfo;
        }

        var sc = raceInfo.ProcessG(parts);
        if (sc is CarLapStateUpdate cls)
            cls.SessionContext = sessionContext;

        // Save off starting positions
        if (raceInfo.Laps == 0 && !RaceHasPassedStart())
        {
            var flag = Heartbeat.FlagStatus.ToFlag();
            startingPositionProcessor.UpdateStartingPosition(parts, regNum, flag);
        }

        return sc;
    }

    /// <summary>
    /// Determine if the race is in progress.
    /// </summary>
    private bool RaceHasPassedStart() => raceInformation.Values.Any(r => r.Laps > 0);

    #endregion

    #region Practice/qualifying information

    /// <summary>
    /// Processes $H messages.
    /// </summary>
    /// <example>$H,2,"1234BE",3,"00:02:17.872"</example>
    private ICarStateChange? ProcessH(string data)
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

        // Allow for reset when the event is initializing. Once it has started,
        // suppress the resets to reduce user confusion
        var flag = Heartbeat.FlagStatus.ToFlag();
        if (flag == Flags.Unknown)
        {
            classes.Clear();
            passingInformation.Clear();
            sessionContext.ClearStartingPositions();
        }
    }

    #endregion

    #region Passing information

    /// <summary>
    /// Processes $J messages.
    /// </summary>
    /// <example>$J,"1234BE","00:02:03.826","01:42:17.672"</example>
    private ICarStateChange? ProcessJ(string data)
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
