using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.TimingAndScoringService.EventStatus.RMonitor;

/// <summary>
/// Responsible for second pass on data from the Result Monitor such as for derived data, e.g. gap, diff, etc.
/// </summary>
public class SecondaryProcessor
{
    private const string MinTimeFormat = @"m\:ss\.fff";
    private const string SecTimeFormat = @"s\.fff";

    private readonly Dictionary<string, CarPosition> carPositionsLookup = [];
    //private List<CarPosition> carPositions = [];

    public List<CarPosition> UpdateCarPositions(List<CarPosition> positions)
    {
        // Diff, gap in class and overall
        // Best TIme
        // Class positions
        // Fastest
        // Positions gained lost in class and overall
        // stale

        foreach (var position in positions)
        {
            if (position.Number != null)
            {
                carPositionsLookup[position.Number] = position;
            }
        }

        // Overall Gap and Difference
        var cps = carPositionsLookup.Values.ToList();
        UpdateGapAndDiff(cps, (p, g) => p.OverallGap = g, (p, d) => p.OverallDifference = d);
        
        // Class Gap and Difference
        var classGroups = cps.GroupBy(p => p.Class);
        foreach (var classGroup in classGroups)
        {
            UpdateGapAndDiff([.. classGroup], (p, g) => p.InClassGap = g, (p, d) => p.InClassDifference = d);
        }

        return positions;
    }

    private void UpdateGapAndDiff(List<CarPosition> carPositions, Action<CarPosition, string> setGap, Action<CarPosition, string> setDiff)
    {
        if (carPositions.Count == 0)
            return;

        carPositions.Sort(new PositionComparer());

        var leader = carPositions[0];
        var leaderTime = ParseRMTime(leader.TotalTime ?? string.Empty);

        for (int i = 0; i < carPositions.Count; i++)
        {
            var currentPosition = carPositions[i];
            var cpt = ParseRMTime(currentPosition.TotalTime ?? string.Empty);
            if (cpt == default)
            {
                continue;
            }

            // Leader
            if (i == 0)
            {
                setGap(currentPosition, string.Empty);
                setDiff(currentPosition, string.Empty);
            }
            else // Behind the leader
            {
                var positionAhead = carPositions[i - 1];
                if (positionAhead.TotalTime == null)
                {
                    continue;
                }

                // Overall Gap
                if (positionAhead.LastLap == currentPosition.LastLap)
                {
                    var pat = ParseRMTime(positionAhead.TotalTime);
                    var g = (cpt - pat);
                    setGap(currentPosition, g.ToString(GetTimeFormat(g)));
                }
                else
                {
                    int laps = positionAhead.LastLap - currentPosition.LastLap;
                    setGap(currentPosition, $"{laps} {GetLapTerm(laps)}");
                }

                // Overall Difference
                if (leader.LastLap == currentPosition.LastLap)
                {
                    var diff = (cpt - leaderTime);
                    setDiff(currentPosition, diff.ToString(GetTimeFormat(diff)));
                }
                else
                {
                    int laps = leader.LastLap - currentPosition.LastLap;
                    setDiff(currentPosition, $"{laps} {GetLapTerm(laps)}");
                }
            }
        }
    }

    private static DateTime ParseRMTime(string time)
    {
        DateTime.TryParseExact(time, "HH:mm:ss.fff", null, DateTimeStyles.None, out var result);
        return result;
    }

    private static string GetTimeFormat(TimeSpan time)
    {
        if (time.Minutes > 0)
        {
            return MinTimeFormat;
        }
        return SecTimeFormat;
    }

    private static string GetLapTerm(int laps)
    {
        if (laps == 1)
        {
            return "lap";
        }
        return "laps";
    }

    /// <summary>
    /// Comparer for sorting car positions. This is used to sort the cars in the DataGrid.
    /// </summary>
    private class PositionComparer : IComparer<CarPosition>
    {
        public int Compare(CarPosition? x, CarPosition? y)
        {
            if (x == null || y == null)
            {
                return 0;
            }

            if (x.OverallPosition == 0)
            {
                return -1;
            }
            return x.OverallPosition.CompareTo(y.OverallPosition);
        }
    }
}
