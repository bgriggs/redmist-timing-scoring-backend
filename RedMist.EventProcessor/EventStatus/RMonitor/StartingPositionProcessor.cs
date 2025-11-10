using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.RMonitor;

/// <summary>
/// Tracks starting positions based on order of cars as they cross s/f during the starting green lap.
/// </summary>
public class StartingPositionProcessor
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionContext;


    public StartingPositionProcessor(SessionContext sessionContext, ILoggerFactory loggerFactory)
    {
        this.sessionContext = sessionContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public virtual void UpdateStartingPosition(string[] parts, string regNum, Flags flag)
    {
        // Allow capture of starting positions during lap 0 up to and including the green flag
        if (flag == Flags.Unknown || flag == Flags.Yellow || flag == Flags.Green)
        {
            // Make a copy for storing off
            var sp = new RaceInformation();
            sp.ProcessG(parts);
            sessionContext.SetStartingPosition(regNum, sp.Position);
            //startingPositions[regNum] = sp;
            UpdateInClassStartingPositionLookup();
        }
    }

    private void UpdateInClassStartingPositionLookup()
    {
        var entries = new List<(string num, string @class, int pos)>();
        var startingPositions = sessionContext.GetStartingPositions();
        foreach (var regNum in startingPositions.Keys)
        {
            var ri = startingPositions[regNum];
            var car = sessionContext.GetCarByNumber(regNum);
            if (car == null || car.Class == null)
            {
                Logger.LogWarning("Car {rn} not found for starting position", regNum);
                continue;
            }
            entries.Add((regNum, car.Class, car.ClassPosition));
        }

        var classGroups = entries.GroupBy(x => x.@class);
        foreach (var classGroup in classGroups)
        {
            var positions = classGroup.OrderBy(x => x.pos).ToList();
            for (int i = 0; i < positions.Count; i++)
            {
                var entry = positions[i];
                sessionContext.SetInClassStartingPosition(entry.num, i + 1);
                //inClassStartingPositions[entry.num] = i + 1;
            }
        }
    }
}
