using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.EventProcessor.EventStatus;

public class CarsConsistencyCheck
{
    public static bool AreCarsConsistent(ImmutableList<CarPosition> cars, ILogger? logger = null)
    {
        if (cars.Count == 0)
            return true;

        // Check that all car positions are unique
        var positions = new Dictionary<int, string>();
        foreach (var car in cars)
        {
            if (positions.TryGetValue(car.OverallPosition, out string? value))
            {
                logger?.LogWarning("Duplicate car position {pos} for {num} and {num2}", car.OverallPosition, value, car.Number);
                return false;
            }
            positions[car.OverallPosition] = car.Number ?? string.Empty;
        }

        int pos = 1;
        var sortedPos = cars.OrderBy(c => c.OverallPosition).ToList();
        foreach (var car in sortedPos)
        {
            if (car.OverallPosition != pos)
            {
                logger?.LogWarning("Car position mismatch: expected {expected}, got {actual} for car {num}", pos, car.OverallPosition, car.Number);
                return false;
            }
            pos++;
        }

        return true;
    }
}
