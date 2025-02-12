using MediatR;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

/// <summary>
/// Result Monitor data processor primarily for Orbits data.
/// </summary>
/// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
public class RmDataProcessor : IDataProcessor
{
    private readonly int eventId;
    private readonly IMediator mediator;
    private ILogger Logger { get; }

    private readonly Dictionary<int, string> classes = [];
    private readonly Dictionary<string, Competitor> competitors = [];
    private int eventReference;
    private string eventName = string.Empty;

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
                    ProcessA(command);
                }
                else if (command.StartsWith("$COMP"))
                {
                    ProcessComp(command);
                }
                else if (command.StartsWith("$B"))
                {
                    ProcessB(command);
                }
                else if (command.StartsWith("$C"))
                {
                    ProcessC(command);
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
        eventReference = int.Parse(parts[1]);
        eventName = parts[2].Replace("\"", "");
    }

    public Event GetEvent()
    {
        return new Event { EventId = eventId, EventName = eventName };
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
}
