using RedMist.Backend.Shared.Utilities;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers reading an event's end date as the calendar day it is.
/// </summary>
/// <remarks>
/// The report job's candidate query, the dashboard's report status and the viewership bounds all rest
/// on these two methods agreeing with each other. Every mistake here is an event that is due a day
/// early or late, or a report that stops on the final morning - never an error.
/// </remarks>
[TestClass]
public class EventDatesTests
{
    private static readonly DateTime Day = new(2026, 9, 20);

    /// <summary>
    /// End dates as they can arrive: midnight as the editor stores them, and times of day on either
    /// side of noon, read back with no Kind as PostgreSQL returns them or with a UTC Kind.
    /// </summary>
    private static IEnumerable<DateTime> EndDates()
    {
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc })
        {
            yield return DateTime.SpecifyKind(Day, kind);
            yield return DateTime.SpecifyKind(Day.AddTicks(1), kind);
            yield return DateTime.SpecifyKind(Day.AddHours(6), kind);
            yield return DateTime.SpecifyKind(Day.AddHours(15.5), kind);
            yield return DateTime.SpecifyKind(Day.AddDays(1).AddTicks(-1), kind);
        }
    }

    [TestMethod]
    [DataRow(0d, DateTimeKind.Unspecified)]
    [DataRow(6d, DateTimeKind.Unspecified)]
    [DataRow(15.5d, DateTimeKind.Unspecified)]
    [DataRow(15.5d, DateTimeKind.Utc)]
    [DataRow(23.99d, DateTimeKind.Utc)]
    public void TheLastDayEnds_AtTheMidnightAfterTheEndDate_WhateverItsTimeOfDay(double hour, DateTimeKind kind)
    {
        var endDate = DateTime.SpecifyKind(Day.AddHours(hour), kind);

        var end = EventDates.LastDayEndUtc(endDate);

        Assert.AreEqual(Day.AddDays(1), end, $"An end date of {endDate:HH:mm} ({kind}) ended its day at {end:MM-dd HH:mm}.");
        Assert.AreEqual(DateTimeKind.Utc, end.Kind);
    }

    /// <summary>
    /// The cutoff is only useful if comparing the stored column with it says exactly what comparing
    /// the end of the last day with the moment would. Checked for every end date against every half
    /// hour across the days around it, and a tick either side of each midnight, where the boundaries
    /// all fall.
    /// </summary>
    [TestMethod]
    public void AnEndDateIsBeforeTheCutoff_ExactlyWhenItsLastDayHadEndedByThatMoment()
    {
        var moments = Enumerable.Range(0, 5 * 48).Select(i => Day.AddDays(-2).AddMinutes(30 * i))
            .Concat(Enumerable.Range(-2, 5).SelectMany(d => new[]
            {
                Day.AddDays(d).AddTicks(-1), Day.AddDays(d), Day.AddDays(d).AddTicks(1),
            }))
            .Select(t => DateTime.SpecifyKind(t, DateTimeKind.Utc))
            .ToList();

        var checkedPairs = 0;
        foreach (var endDate in EndDates())
        {
            foreach (var moment in moments)
            {
                var byCutoff = endDate < EventDates.LastDayEndedByCutoff(moment);
                var byDayEnd = EventDates.LastDayEndUtc(endDate) <= moment;
                Assert.AreEqual(byDayEnd, byCutoff,
                    $"End date {endDate:MM-dd HH:mm:ss.fffffff} ({endDate.Kind}) at {moment:MM-dd HH:mm:ss.fffffff}.");
                checkedPairs++;
            }
        }

        Assert.IsTrue(checkedPairs > 2000, "The grid is too small to prove anything.");
    }

    [TestMethod]
    public void TheCutoffIsAMidnight_InUtc()
    {
        var cutoff = EventDates.LastDayEndedByCutoff(DateTime.SpecifyKind(Day.AddHours(17), DateTimeKind.Utc));

        Assert.AreEqual(Day, cutoff);
        Assert.AreEqual(DateTimeKind.Utc, cutoff.Kind);
    }
}
