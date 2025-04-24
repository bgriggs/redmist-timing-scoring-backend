using MediatR;
using RedMist.ControlLogs;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingAndScoringService.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.X2;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Result Monitor data format processor primarily for Orbits data.
/// </summary>
/// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
public class OrbitsDataProcessor : IDataProcessor
{
    public int EventId { get; private set; }
    public int SessionId => sessionMonitor.SessionId;
    private readonly IMediator mediator;

    private ILogger Logger { get; }
    private readonly Debouncer debouncer = new(TimeSpan.FromMilliseconds(80));
    public Debouncer Debouncer => debouncer;

    private readonly SemaphoreSlim _lock = new(1, 1);
    public Heartbeat Heartbeat { get; } = new();
    private readonly Dictionary<int, string> classes = [];
    private readonly Dictionary<string, Competitor> competitors = [];
    private readonly Dictionary<string, RaceInformation> raceInformation = [];
    private readonly Dictionary<string, RaceInformation> startingPositions = [];
    private readonly Dictionary<string, int> inClassStartingPositions = [];
    private readonly Dictionary<string, PracticeQualifying> practiceQualifying = [];
    private readonly Dictionary<string, PassingInformation> passingInformation = [];


    public int SessionReference { get; set; }
    public string SessionName { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public double TrackLength { get; set; }

    public PitProcessor PitProcessor { get; private set; }

    private readonly SessionMonitor sessionMonitor;
    private readonly ControlLogCache? controlLog;
    private readonly FlagProcessor flagProcessor;
    public CompetitorMetadataProcessor CompetitorMetadataProcessor { get; private set; }
    private readonly HashSet<uint> lastTransponderPassings = [];
    private readonly PositionMetadataProcessor secondaryProcessor = new();


    public OrbitsDataProcessor(int eventId, IMediator mediator, ILoggerFactory loggerFactory, SessionMonitor sessionMonitor,
        PitProcessor pitProcessor, ControlLogCache? controlLog, FlagProcessor flagProcessor, CompetitorMetadataProcessor competitorMetadataProcessor)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        EventId = eventId;
        this.mediator = mediator;
        this.sessionMonitor = sessionMonitor;
        PitProcessor = pitProcessor;
        this.controlLog = controlLog;
        this.flagProcessor = flagProcessor;
        CompetitorMetadataProcessor = competitorMetadataProcessor;
    }


    public async Task ProcessUpdate(string type, string data, int sessionId, CancellationToken stoppingToken = default)
    {
        var sw = Stopwatch.StartNew();
        await sessionMonitor.ProcessSessionAsync(sessionId, stoppingToken);

        // Parse RMonitor data
        if (type == "rmonitor")
        {
            await ProcessResultMonitorAsync(data, stoppingToken);
        }
        // Passings
        else if (type == "x2pass")
        {
            await ProcessPassings(data, stoppingToken);
        }
        // Loops
        else if (type == "x2loop")
        {
            ProcessLoops(data, stoppingToken);
        }
        // Flags
        else if (type == "flags")
        {
            _ = UpdateFlagsAsync(data, stoppingToken);
        }
        // Competitor Metadata
        else if (type == "competitors")
        {
            _ = ProcessCompetitorMetadataAsync(data, stoppingToken);
        }

        Logger.LogTrace("Processed {type} in {time}ms", type, sw.ElapsedMilliseconds);
    }

    private async Task ProcessPassings(string data, CancellationToken stoppingToken)
    {
        await Task.Run(() =>
        {
            try
            {
                var passings = JsonSerializer.Deserialize<List<Passing>>(data);
                if (passings != null)
                {
                    _ = mediator.Publish(new X2PassingsNotification(passings), stoppingToken);
                    PitProcessor.UpdatePassings(passings);

                    lock (lastTransponderPassings)
                    {
                        foreach (var passing in passings)
                        {
                            lastTransponderPassings.Add(passing.TransponderId);
                        }
                    }

                    _ = debouncer.ExecuteAsync(() => Task.Run(() => PublishChanges(stoppingToken)), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing X2 passings data");
            }
        }, stoppingToken);
    }

    private void ProcessLoops(string data, CancellationToken stoppingToken)
    {
        try
        {
            var loops = JsonSerializer.Deserialize<List<Loop>>(data);
            if (loops != null)
            {
                _ = mediator.Publish(new X2LoopsNotification(loops), stoppingToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing X2 loop data");
        }
    }

    private async Task ProcessResultMonitorAsync(string data, CancellationToken stoppingToken)
    {
        await _lock.WaitAsync(stoppingToken);
        try
        {
            var commands = data.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var command in commands)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;
                try
                {
                    if (command.StartsWith("$F"))
                    {
                        // Heartbeat message
                        ProcessF(command);
                    }
                    else if (command.StartsWith("$A"))
                    {
                        // Competitor information
                        ProcessA(command);
                    }
                    else if (command.StartsWith("$COMP"))
                    {
                        // Competitor information
                        ProcessComp(command);
                    }
                    else if (command.StartsWith("$B"))
                    {
                        // Event information
                        ProcessB(command);
                    }
                    else if (command.StartsWith("$C"))
                    {
                        // Class information
                        ProcessC(command);
                    }
                    else if (command.StartsWith("$E"))
                    {
                        // Setting (track) information
                        ProcessE(command);
                    }
                    else if (command.StartsWith("$G"))
                    {
                        // Race information
                        ProcessG(command);
                    }
                    else if (command.StartsWith("$H"))
                    {
                        // Practice/qualifying information
                        ProcessH(command);
                    }
                    else if (command.StartsWith("$I"))
                    {
                        // Init record (reset)
                        ProcessI();
                    }
                    else if (command.StartsWith("$J"))
                    {
                        // Passing information
                        ProcessJ(command);
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
        }
        finally
        {
            _lock.Release();
        }

        _ = debouncer.ExecuteAsync(() => Task.Run(() => PublishChanges(stoppingToken)), stoppingToken);
    }

    private async Task ProcessCompetitorMetadataAsync(string data, CancellationToken stoppingToken)
    {
        await Task.Run(async () =>
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<List<CompetitorMetadata>>(data);
                if (metadata != null)
                {
                    await CompetitorMetadataProcessor.Process(metadata, stoppingToken);
                }
                else
                {
                    Logger.LogWarning("Competitor metadata is null. Possible deserialization issue.");
                }
            }
            catch (JsonException ex)
            {
                Logger.LogError(ex, "Error processing competitor metadata JSON");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing competitor metadata");
            }
        }, stoppingToken);
    }

    #region Result Monitor
    #region Heartbeat message

    /// <summary>
    /// Processes $F –- Heartbeat message
    /// </summary>
    /// <example>$F,14,"00:12:45","13:34:23","00:09:47","Green "</example>
    private void ProcessF(string data)
    {
        Heartbeat.ProcessF(data);
    }

    public TimingCommon.Models.EventStatus GetEventStatus()
    {
        var flag = Heartbeat.FlagStatus.ToFlag();
        return new TimingCommon.Models.EventStatus
        {
            EventId = EventId.ToString(),
            Flag = flag,
            LapsToGo = Heartbeat.LapsToGo,
            TimeToGo = Heartbeat.TimeToGo,
            LocalTimeOfDay = Heartbeat.TimeOfDay,
            RunningRaceTime = Heartbeat.RaceTime,
            IsPracticeOrQualifying = SessionHelper.IsPracticeOrQualifyingSession(SessionName),
        };
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

    public EventEntry[] GetEventEntries()
    {
        return competitors.Select(c => c.Value.ToEventEntry(GetClassName))
                          .ToArray()!;
    }

    public EventEntry[] GetChangedEventEntries()
    {
        return competitors.Select(c => c.Value.ToEventEntryWhenDirtyWithReset(GetClassName))
                          .Where(c => c != null)
                          .ToArray()!;
    }

    private string? GetClassName(int classId)
    {
        if (classes.TryGetValue(classId, out var className))
        {
            return className;
        }
        return null;
    }

    #endregion

    #region Event Name

    /// <summary>
    /// Processes $B messages -- Run Information.
    /// </summary>
    /// <example>$B,5,"Friday free practice"</example>
    private void ProcessB(string data)
    {
        var parts = data.Split(',');
        SessionReference = int.Parse(parts[1]);
        SessionName = parts[2].Replace("\"", "");
    }

    public Event GetEvent()
    {
        return new Event { EventId = EventId, EventName = SessionName };
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

    public ImmutableDictionary<int, string> GetClasses()
    {
        return classes.ToImmutableDictionary();
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
    private void ProcessG(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[2].Replace("\"", "");
        if (!raceInformation.TryGetValue(regNum, out var raceInfo))
        {
            raceInfo = new RaceInformation();
            raceInformation[regNum] = raceInfo;
        }
        raceInfo.ProcessG(parts);

        // Save off starting positions
        if (raceInfo.Laps == 0)
        {
            var flag = Heartbeat.FlagStatus.ToFlag();

            // Allow capture of starting positions during lap 0 up to and include the green flag
            if (flag == Flags.Unknown || flag == Flags.Yellow || flag == Flags.Green)
            {
                // Make a copy for storing off
                var sp = new RaceInformation();
                sp.ProcessG(parts);
                startingPositions[regNum] = sp;
                UpdateInClassStartingPositionLookup();
            }
        }
    }

    public ImmutableDictionary<string, RaceInformation> GetRaceInformation()
    {
        return raceInformation.ToImmutableDictionary();
    }

    public ImmutableDictionary<string, RaceInformation> GetOverallStartingPositions()
    {
        return startingPositions.ToImmutableDictionary();
    }

    private void UpdateInClassStartingPositionLookup()
    {
        var entries = new List<(string num, int @class, int pos)>();
        foreach (var regNum in startingPositions.Keys)
        {
            var ri = startingPositions[regNum];
            if (!competitors.TryGetValue(regNum, out var comp))
            {
                Logger.LogWarning("Competitor {rn} not found for starting position", regNum);
                continue;
            }
            entries.Add((regNum, comp.ClassNumber, ri.Position));
        }

        var classGroups = entries.GroupBy(x => x.@class);
        foreach (var classGroup in classGroups)
        {
            var positions = classGroup.OrderBy(x => x.pos).ToList();
            for (int i = 0; i < positions.Count; i++)
            {
                var entry = positions[i];
                inClassStartingPositions[entry.num] = i + 1;
            }
        }
    }

    public ImmutableDictionary<string, int> GetInClassStartingPositions()
    {
        return inClassStartingPositions.ToImmutableDictionary();
    }

    #endregion

    #region Practice/qualifying information

    /// <summary>
    /// Processes $H messages.
    /// </summary>
    /// <example>$H,2,"1234BE",3,"00:02:17.872"</example>
    private void ProcessH(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[2].Replace("\"", "");
        if (!practiceQualifying.TryGetValue(regNum, out var pq))
        {
            pq = new PracticeQualifying();
            practiceQualifying[regNum] = pq;
        }
        pq.ProcessH(parts);
    }

    public ImmutableDictionary<string, PracticeQualifying> GetPracticeQualifying()
    {
        return practiceQualifying.ToImmutableDictionary();
    }

    #endregion

    #region Init record

    /// <summary>
    /// Processes $I messages.
    /// </summary>
    /// <example>$I,"16:36:08.000","12 jan 01"</example>
    private void ProcessI()
    {
        var flag = Heartbeat.FlagStatus.ToFlag();

        // Allow for reset when the event is initializing. Once it has started,
        // suppress the resets to reduce user confusion
        if (flag == Flags.Unknown)
        {
            classes.Clear();
            competitors.Clear();
            raceInformation.Clear();
            practiceQualifying.Clear();
            passingInformation.Clear();
            startingPositions.Clear();
            inClassStartingPositions.Clear();
            secondaryProcessor.Clear();
        }
        PublishEventReset();
    }

    #endregion

    #region Passing information

    /// <summary>
    /// Processes $J messages.
    /// </summary>
    /// <example>$J,"1234BE","00:02:03.826","01:42:17.672"</example>
    private void ProcessJ(string data)
    {
        var parts = data.Split(',');
        var regNum = parts[1].Replace("\"", "");
        if (!passingInformation.TryGetValue(regNum, out var pq))
        {
            pq = new PassingInformation();
            passingInformation[regNum] = pq;
        }
        pq.ProcessJ(parts);
    }

    public ImmutableDictionary<string, PassingInformation> GetPassingInformation()
    {
        return passingInformation.ToImmutableDictionary();
    }

    #endregion

    #region Corrected Finish Time

    /// <summary>
    /// Processes $COR messages.
    /// </summary>
    /// <example>$COR,"123BE","658",2,"00:00:35.272","+00:00:00.012"</example>
    private void ProcessCor(string data)
    {
    }

    #endregion
    #endregion

    #region Flags

    private async Task UpdateFlagsAsync(string json, CancellationToken stoppingToken)
    {
        var fs = JsonSerializer.Deserialize<List<FlagDuration>>(json);
        if (fs != null)
        {
            await flagProcessor.ProcessFlags(SessionId, fs, stoppingToken);
        }
    }

    #endregion

    #region Status Publishing

    private async Task PublishChanges(CancellationToken stoppingToken = default)
    {
        TimingCommon.Models.EventStatus eventStatus;
        EventEntry[] eventEntries;
        CarPosition[] carPositions;

        await _lock.WaitAsync(stoppingToken);
        try
        {
            // Event Status
            eventStatus = GetEventStatus();

            // Event Entries
            eventEntries = GetChangedEventEntries();

            // Car Positions
            carPositions = await GetCarPositions(includeChangedOnly: true);

            // Loop data (pit)
            PitProcessor.ApplyTransponderPassing(carPositions);
        }
        finally
        {
            _lock.Release();
        }

        // Put flag state on all car positions
        foreach (var carPosition in carPositions)
        {
            carPosition.Flag = eventStatus.Flag;
        }

        var payload = new Payload
        {
            EventId = EventId,
            EventStatus = eventStatus,
        };
        payload.EventEntryUpdates.AddRange(eventEntries);
        payload.CarPositionUpdates.AddRange(carPositions);

        var json = JsonSerializer.Serialize(payload);
        _ = mediator.Publish(new StatusNotification(EventId, SessionId, json) { Payload = payload, PitProcessor = PitProcessor }, stoppingToken);
    }

    public void PublishEventReset(CancellationToken stoppingToken = default)
    {
        var payload = new Payload { EventId = EventId, IsReset = true };
        var json = JsonSerializer.Serialize(payload);
        _ = mediator.Publish(new StatusNotification(EventId, SessionId, json) { PitProcessor = PitProcessor }, stoppingToken);
    }

    public async Task<CarPosition[]> GetCarPositions(bool includeChangedOnly = false)
    {
        var carPositions = new List<CarPosition>();
        List<uint> dirtyTransponderPassings;
        lock (lastTransponderPassings)
        {
            dirtyTransponderPassings = [.. lastTransponderPassings];
        }

        foreach (var reg in raceInformation.Keys)
        {
            if (!raceInformation.TryGetValue(reg, out var raceInfo))
            {
                continue;
            }

            var carPos = new CarPosition
            {
                EventId = EventId.ToString(),
                SessionId = SessionId.ToString(),
                Number = raceInfo.RegistrationNumber,
                OverallPosition = raceInfo.Position,
                TotalTime = raceInfo.RaceTime,
                LastLap = raceInfo.Laps,
                Class = GetCarsClass(raceInfo.RegistrationNumber),
            };

            // Transponder
            if (competitors.TryGetValue(reg, out var competitor))
            {
                carPos.TransponderId = competitor.Transponder;
            }

            // Starting position
            if (startingPositions.TryGetValue(reg, out var startingPos))
            {
                carPos.OverallStartingPosition = startingPos.Position;
            }
            if (inClassStartingPositions.TryGetValue(reg, out var inClassPos))
            {
                carPos.InClassStartingPosition = inClassPos;
            }

            // Last lap time
            if (passingInformation.TryGetValue(reg, out var pass))
            {
                if (carPos.TotalTime != pass.RaceTime)
                {
                    //Logger.LogWarning("Total time mismatch for passingInformation {0}: {1} != {2}", reg, carPos.TotalTime, pass.RaceTime);
                }
                carPos.LastTime = pass.LapTime;
            }

            // Best time
            if (practiceQualifying.TryGetValue(reg, out var pq))
            {
                carPos.BestTime = pq.BestLapTime;
                carPos.BestLap = pq.BestLap;
                carPos.IsBestTime = carPos.BestLap == carPos.LastLap;
            }

            if (includeChangedOnly)
            {
                if (raceInfo.IsDirty || pass != null && pass.IsDirty || pq != null && pq.IsDirty || dirtyTransponderPassings.Contains(carPos.TransponderId))
                {
                    carPositions.Add(carPos);
                }
            }
            else
            {
                carPositions.Add(carPos);
            }

            raceInfo.IsDirty = false;
            if (pass != null)
            {
                pass.IsDirty = false;
            }
            if (pq != null)
            {
                pq.IsDirty = false;
            }
        }

        // Apply diff / gap
        carPositions = secondaryProcessor.UpdateCarPositions(carPositions);

        // Apply penalties
        if (controlLog != null)
        {
            var penalties = await controlLog.GetPenaltiesAsync();
            foreach (var carPosition in carPositions)
            {
                var num = carPosition.Number?.ToLower();
                if (num != null && penalties.TryGetValue(num, out var penalty))
                {
                    carPosition.PenalityWarnings = penalty.warnings;
                    carPosition.PenalityLaps = penalty.laps;
                }
            }
        }

        lock (lastTransponderPassings)
        {
            foreach (var transponder in dirtyTransponderPassings)
            {
                lastTransponderPassings.Remove(transponder);
            }
        }

        return [.. carPositions];
    }

    /// <summary>
    /// Gets full update of current status.
    /// </summary>
    public async Task<Payload> GetPayload(CancellationToken stoppingToken = default)
    {
        var payload = new Payload();
        await _lock.WaitAsync(stoppingToken);
        try
        {
            // Event Status
            var eventStatus = GetEventStatus();

            // Event Entries
            var eventEntries = GetEventEntries();

            // Car Positions
            var carPositions = await GetCarPositions();

            // Loop data (pit)
            PitProcessor.ApplyTransponderPassing(carPositions);

            // Put flag state on all car positions
            foreach (var carPosition in carPositions)
            {
                carPosition.Flag = eventStatus.Flag;
            }

            payload.EventId = EventId;
            payload.EventName = SessionName;
            payload.EventStatus = eventStatus;
            payload.EventEntries.AddRange(eventEntries);
            payload.CarPositions.AddRange(carPositions);

            payload.FlagDurations = await flagProcessor.GetFlagsAsync(stoppingToken);

        }
        finally
        {
            _lock.Release();
        }

        sessionMonitor.SetCurrentPayload(payload);
        return payload;
    }

    private string? GetCarsClass(string number)
    {
        if (competitors.TryGetValue(number, out var competitor))
        {
            classes.TryGetValue(competitor.ClassNumber, out var className);
            return className;
        }
        return null;
    }

    #endregion
}
