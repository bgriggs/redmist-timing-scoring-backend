using BigMission.Shared.Utilities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.X2;
using StackExchange.Redis;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
    private readonly FlagProcessor flagProcessor;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HashSet<uint> lastTransponderPassings = [];
    private readonly PositionMetadataProcessor secondaryProcessor = new();
    private bool startingPositionsInitialized = false;
    private DateTime lastResetPosition = DateTime.MinValue;
    private DateTime lastPositionMismatch = DateTime.MinValue;
    private HashEntry[]? lastPenalties;


    public OrbitsDataProcessor(int eventId, IMediator mediator, ILoggerFactory loggerFactory, SessionMonitor sessionMonitor,
        PitProcessor pitProcessor, FlagProcessor flagProcessor, IConnectionMultiplexer cacheMux, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        EventId = eventId;
        this.mediator = mediator;
        this.sessionMonitor = sessionMonitor;
        PitProcessor = pitProcessor;
        this.flagProcessor = flagProcessor;
        this.cacheMux = cacheMux;
        this.tsContext = tsContext;
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
        else if (type == "x2loop") { }
        // Flags
        else if (type == "flags")
        {
            _ = UpdateFlagsAsync(data, stoppingToken);
        }
        // Competitor Metadata
        else if (type == "competitors") { }

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
                    PitProcessor.UpdatePassings(passings);

                    lock (lastTransponderPassings)
                    {
                        foreach (var passing in passings)
                        {
                            lastTransponderPassings.Add(passing.TransponderId);
                        }
                    }

                    _ = debouncer.ExecuteAsync(() => PublishChanges(stoppingToken));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing X2 passings data");
            }
        }, stoppingToken);
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
            IsPracticeQualifying = SessionHelper.IsPracticeOrQualifyingSession(SessionName),
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
        if (raceInfo.Laps == 0 && !RaceHasPassedStart())
        {
            var flag = Heartbeat.FlagStatus.ToFlag();
            UpdateStartingPosition(parts, regNum, flag);
        }
    }

    private void UpdateStartingPosition(string[] parts, string regNum, Flags flag)
    {
        // Allow capture of starting positions during lap 0 up to and including the green flag
        if (flag == Flags.Unknown || flag == Flags.Yellow || flag == Flags.Green)
        {
            // Make a copy for storing off
            var sp = new RaceInformation();
            sp.ProcessG(parts);
            startingPositions[regNum] = sp;
            UpdateInClassStartingPositionLookup();
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

            // Keep as if statements to avoid issues with docker build
            if (pass != null)
                pass.IsDirty = false;
            if (pq != null)
                pq.IsDirty = false;
        }

        // Apply diff / gap / position changes
        // See if a reload of the starting positions are needed, such as after a service restart
        if (SessionId > 0 && !AreStartPositionsInitialized() && RaceHasPassedStart() && !startingPositionsInitialized)
        {
            Logger.LogDebug("Possible service restart, attempting to initialize starting positions from logged data");
            await InitializeStartingPositions();
        }
        carPositions = secondaryProcessor.UpdateCarPositions(carPositions);

        // Apply penalties
        await UpdateCarsWithPenalties(carPositions, !includeChangedOnly);

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
    /// Get penalties from redis and apply to car positions.
    /// </summary>
    /// <param name="carPositions"></param>
    /// <param name="fullUpdate">full will pull latest logs</param>
    private async Task UpdateCarsWithPenalties(List<CarPosition> carPositions, bool fullUpdate)
    {
        try
        {
            HashEntry[]? controlLogHash = null;
            if (fullUpdate || lastPenalties == null)
            {
                var cache = cacheMux.GetDatabase();
                var carLogCacheKey = string.Format(Backend.Shared.Consts.CONTROL_LOG_CAR_PENALTIES, EventId);
                controlLogHash = await cache.HashGetAllAsync(carLogCacheKey);
                lastPenalties = controlLogHash;
                Logger.LogTrace("Retrieved {count} control log penalties from Redis", controlLogHash.Length);
            }
            else if (lastPenalties != null)
            {
                controlLogHash = lastPenalties;
            }

            foreach (var entry in controlLogHash!)
            {
                try
                {
                    var car = carPositions.FirstOrDefault(c => (c.Number?.ToLower() ?? string.Empty).Equals(entry.Name.ToString(), StringComparison.CurrentCultureIgnoreCase));
                    if (car != null && entry.Value.HasValue)
                    {
                        var penality = JsonSerializer.Deserialize<CarPenality>(entry.Value.ToString()!);
                        if (penality != null)
                        {
                            car.PenalityWarnings = penality.Warnings;
                            car.PenalityLaps = penality.Laps;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing control log entry for car {car}", entry.Name);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error retrieving control log penalties from Redis");
        }
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

        // Perform consistency check
        var isConsistent = PerformConsistencyCheck(payload);
        if (!isConsistent)
        {
            Logger.LogWarning("Inconsistent payload detected for EventId {eventId}. Initiating reset from relay.", EventId);
            _ = ResetRacePositionState();
        }

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

    /// <summary>
    /// Look for errors or issues in the position information.
    /// </summary>
    /// <param name="payload"></param>
    /// <returns></returns>
    private bool PerformConsistencyCheck(Payload payload)
    {
        if (payload.CarPositions.Count == 0)
        {
            return true;
        }

        // Check that all car positions are unique
        var positions = new Dictionary<int, string>();
        foreach (var car in payload.CarPositions)
        {
            if (positions.TryGetValue(car.OverallPosition, out string? value))
            {
                Logger.LogWarning("Duplicate car position {pos} for {num} and {num2}", car.OverallPosition, value, car.Number);
                return false;
            }
            positions[car.OverallPosition] = car.Number ?? string.Empty;
        }

        bool isMismatch = false;
        int pos = 1;
        var sortedPos = payload.CarPositions.OrderBy(c => c.OverallPosition).ToList();
        foreach (var car in sortedPos)
        {
            if (car.OverallPosition != pos)
            {
                Logger.LogWarning("Car position mismatch: expected {expected}, got {actual} for car {num}", pos, car.OverallPosition, car.Number);

                // Since position changes are not perfectly atomic, do not immediately trigger a reset.
                // Allow a small window for position mismatches to avoid false positives.
                // If the last position mismatch was within the last minute, go ahead and trigger a reset.
                if ((DateTime.Now - lastPositionMismatch).TotalMinutes < 1)
                {
                    lastPositionMismatch = DateTime.Now;
                    return false;
                }

                lastPositionMismatch = DateTime.Now;
                isMismatch = true;
            }
            pos++;
        }

        // Reset on non-consecutive position mismatch occurrences
        if (!isMismatch)
        {
            lastPositionMismatch = DateTime.MinValue; // Reset mismatch time if no issues found
        }

        return true;
    }

    /// <summary>
    /// Resets the race position state by clearing all race-related data and requesting a full update from the relay.
    /// </summary>
    /// <remarks>This method ensures that the race position state is reset only if at least one minute has
    /// passed  since the last reset. It clears the internal collections for race information and practice/qualifying
    /// data,  and publishes a reset event to notify other components.</remarks>
    private async Task ResetRacePositionState()
    {
        if ((DateTime.Now - lastResetPosition).TotalMinutes < 1)
        {
            Logger.LogWarning("ResetRacePositionState called too soon after last reset. Skipping.");
            return;
        }
        lastResetPosition = DateTime.Now;

        await _lock.WaitAsync();
        try
        {
            raceInformation.Clear();
            practiceQualifying.Clear();
        }
        finally
        {
            _lock.Release();
        }

        // Publish reset event
        await mediator.Publish(new RelayResetRequest { EventId = EventId }, CancellationToken.None);
    }

    /// <summary>
    /// Determine if the race is in progress.
    /// </summary>
    private bool RaceHasPassedStart()
    {
        foreach (var ri in raceInformation.Values)
        {
            // Use car that has passes one lap to ensure there was a chance for the race to initialize normally
            if (ri.Laps > 1)
            {
                return true;
            }
        }

        return false;
    }

    private bool AreStartPositionsInitialized()
    {
        return startingPositions.Count == raceInformation.Count;
    }

    private async Task InitializeStartingPositions()
    {
        try
        {
            // Load laps from database for lap zero to replay the start
            var db = await tsContext.CreateDbContextAsync();
            var laps = db.CarLapLogs
                .Where(l => l.EventId == EventId && l.SessionId == SessionId && l.LapNumber == 0)
                .OrderBy(l => l.Timestamp);

            Logger.LogDebug("Loaded {count} laps from database for Event {eventId} Session {s} for starting position initialization", laps.Count(), EventId, SessionId);
            foreach (var lap in laps)
            {
                try
                {
                    var pos = JsonSerializer.Deserialize<CarPosition>(lap.LapData);
                    if (pos != null)
                    {
                        string g = $"$G,{pos.OverallPosition},\"{lap.CarNumber}\",{lap.LapNumber},\"00:00:00.000\"";
                        var parts = g.Split(',');
                        UpdateStartingPosition(parts, lap.CarNumber, (Flags)lap.Flag);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing lap data for car {car}", lap.CarNumber);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initializing starting positions from database");
        }
        finally
        {
            startingPositionsInitialized = true;
        }
    }
}
