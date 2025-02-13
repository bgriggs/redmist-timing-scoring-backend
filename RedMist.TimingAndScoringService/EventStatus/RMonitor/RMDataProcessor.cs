using MediatR;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

/// <summary>
/// Result Monitor data format processor primarily for Orbits data.
/// </summary>
/// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
public class RmDataProcessor : IDataProcessor
{
    private readonly int eventId;
    private readonly IMediator mediator;
    private ILogger Logger { get; }

    private readonly Dictionary<int, string> classes = [];
    private readonly Dictionary<string, Competitor> competitors = [];
    public int EventReference { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public double TrackLength { get; set; }

    public RmDataProcessor(int eventId, IMediator mediator, ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.eventId = eventId;
        this.mediator = mediator;
    }

    public async Task ProcessUpdate(string data, CancellationToken stoppingToken = default)
    {
        // Parse data and send to mediator

        var commands = data.Split('\n');
        foreach (var command in commands)
        {
            try
            {
                if (command.StartsWith("$A"))
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
                    ProcessI(command);
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

        // when shared model is invalidated, save to redis
        // controller needs to load from redis when a new client connects
        // send invalidated shared models to clients
        await mediator.Publish(new StatusNotification(1, data), stoppingToken);
    }

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
        var entries = new List<EventEntry>();

        foreach (var competitor in competitors.Values)
        {
            var entry = competitor.ToEventEntry();
            if (int.TryParse(entry.Class, out var c))
            {
                if (classes.TryGetValue(c, out var className))
                {
                    entry.Class = className;
                }
            }
            entries.Add(entry);
        }

        return [.. entries];
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
        return new Event { EventId = eventId, EventName = EventName };
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
        var className = parts[2].Replace("\"", "");
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
    private void ProcessG(string data)
    {
        var parts = data.Split(',');

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

    }

    #endregion

    #region Init record

    /// <summary>
    /// Processes $I messages.
    /// </summary>
    /// <example>$I,"16:36:08.000","12 jan 01"</example>
    private void ProcessI(string data)
    {
        var parts = data.Split(',');

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

    }

    #endregion

    #region Corrected Finish Time

    /// <summary>
    /// Processes $COR messages.
    /// </summary>
    /// <example>$COR,"123BE","658",2,"00:00:35.272","+00:00:00.012"</example>
    private void ProcessCor(string data)
    {
        var parts = data.Split(',');

    }

    #endregion
}
