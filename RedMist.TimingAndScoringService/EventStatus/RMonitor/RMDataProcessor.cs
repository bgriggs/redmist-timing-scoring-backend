using MediatR;
using RedMist.TimingAndScoringService.Utilities;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

/// <summary>
/// Result Monitor data format processor primarily for Orbits data.
/// </summary>
/// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
public class RmDataProcessor : IDataProcessor
{
    public int EventId { get; private set; }
    private readonly IMediator mediator;
    private ILogger Logger { get; }
    private readonly Debouncer debouncer = new(TimeSpan.FromMilliseconds(100));
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

    public int EventReference { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public double TrackLength { get; set; }

    private readonly SecondaryProcessor secondaryProcessor = new();

    public RmDataProcessor(int eventId, IMediator mediator, ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        EventId = eventId;
        this.mediator = mediator;
    }

    public async Task ProcessUpdate(string data, CancellationToken stoppingToken = default)
    {
        //var sw = Stopwatch.StartNew();
        // Parse data
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
                        Logger.LogWarning("Unknown command: {0}", command);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing command: {0}", command);
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        _ = debouncer.ExecuteAsync(() => Task.Run(() => PublishChanges(stoppingToken)), stoppingToken);

        //Logger.LogInformation("Processed in {0}ms", sw.ElapsedMilliseconds);
    }

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
        Enum.TryParse(typeof(Flags), Heartbeat.FlagStatus, true, out var flag);
        flag ??= Flags.Unknown;

        return new TimingCommon.Models.EventStatus
        {
            EventId = EventId.ToString(),
            Flag = (Flags)flag,
            LapsToGo = Heartbeat.LapsToGo,
            TimeToGo = Heartbeat.TimeToGo,
            TotalTime = Heartbeat.RaceTime
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
        EventReference = int.Parse(parts[1]);
        EventName = parts[2].Replace("\"", "");
    }

    public Event GetEvent()
    {
        return new Event { EventId = EventId, EventName = EventName };
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
        if (raceInfo.IsStartingPosition)
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
                Logger.LogWarning("Competitor {0} not found for starting position", regNum);
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
        classes.Clear();
        competitors.Clear();
        raceInformation.Clear();
        practiceQualifying.Clear();
        passingInformation.Clear();
        startingPositions.Clear();
        inClassStartingPositions.Clear();
        secondaryProcessor.Clear();

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
            carPositions = GetCarPositions(includeChangedOnly: true);
        }
        finally
        {
            _lock.Release();
        }

        var payload = new Payload
        {
            EventId = EventId,
            EventStatus = eventStatus,
        };
        payload.EventEntryUpdates.AddRange(eventEntries);
        payload.CarPositionUpdates.AddRange(carPositions);

        // when shared model is invalidated, save to redis
        // controller needs to load from redis when a new client connects
        var json = JsonSerializer.Serialize(payload);
        _ = mediator.Publish(new StatusNotification(EventId, json), stoppingToken);
    }

    public void PublishEventReset(CancellationToken stoppingToken = default)
    {
        var payload = new Payload { EventId = EventId, IsReset = true };
        var json = JsonSerializer.Serialize(payload);
        _ = mediator.Publish(new StatusNotification(EventId, json), stoppingToken);
    }

    public CarPosition[] GetCarPositions(bool includeChangedOnly = false)
    {
        var carPositions = new List<CarPosition>();
        foreach (var reg in raceInformation.Keys)
        {
            if (!raceInformation.TryGetValue(reg, out var raceInfo))
            {
                continue;
            }

            var carPos = new CarPosition
            {
                EventId = EventId.ToString(),
                Number = raceInfo.RegistrationNumber,
                OverallPosition = raceInfo.Position,
                TotalTime = raceInfo.RaceTime,
                LastLap = raceInfo.Laps,
                Class = GetCarsClass(raceInfo.RegistrationNumber),
            };

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
                if (raceInfo.IsDirty || (pass != null && pass.IsDirty) || (pq != null && pq.IsDirty))
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

        carPositions = secondaryProcessor.UpdateCarPositions(carPositions);

        return [.. carPositions];
    }

    /// <summary>
    /// Gets full update of current status.
    /// </summary>
    public async Task<Payload> GetPayload(CancellationToken stoppingToken = default)
    {
        await _lock.WaitAsync(stoppingToken);
        try
        {
            // Event Status
            var eventStatus = GetEventStatus();

            // Event Entries
            var eventEntries = GetEventEntries();

            // Car Positions
            var carPositions = GetCarPositions();

            var payload = new Payload
            {
                EventId = EventId,
                EventName = EventName,
                EventStatus = eventStatus,
            };
            payload.EventEntries.AddRange(eventEntries);
            payload.CarPositions.AddRange(carPositions);

            return payload;
        }
        finally
        {
            _lock.Release();
        }
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
}
