using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventOrchestration.Services;
using RedMist.EventProcessor.Tests.Utilities;
using System.Globalization;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Services;

/// <summary>
/// The relay log cleanup only runs at noon Mountain on Tuesday, Wednesday or Thursday. The schedule
/// arithmetic decides when a restarted pod next does work, so the day and noon boundaries are pinned here.
/// </summary>
[TestClass]
public class RelayLogCleanupServiceTests
{
    private RelayLogCleanupService service = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"RelayLogCleanupServiceTests_{Guid.NewGuid()}")
            .Options;
        service = new RelayLogCleanupService(loggerFactory.Object, new TestDbContextFactory(options));
    }

    [TestMethod]
    // Before noon on a run day: run today.
    [DataRow("2026-08-04 09:00:00", "2026-08-04 12:00:00", DisplayName = "Tuesday morning runs today")]
    [DataRow("2026-08-04 11:59:59", "2026-08-04 12:00:00", DisplayName = "One second before noon still runs today")]
    [DataRow("2026-08-05 00:00:00", "2026-08-05 12:00:00", DisplayName = "Wednesday midnight runs today")]
    [DataRow("2026-08-06 08:00:00", "2026-08-06 12:00:00", DisplayName = "Thursday morning runs today")]
    // Exactly noon counts as already past, so the next run day is used.
    [DataRow("2026-08-04 12:00:00", "2026-08-05 12:00:00", DisplayName = "Tuesday at noon rolls to Wednesday")]
    [DataRow("2026-08-05 12:00:00", "2026-08-06 12:00:00", DisplayName = "Wednesday at noon rolls to Thursday")]
    [DataRow("2026-08-06 12:00:00", "2026-08-11 12:00:00", DisplayName = "Thursday at noon skips the weekend to Tuesday")]
    // Non-run days always land on the next Tuesday.
    [DataRow("2026-08-03 09:00:00", "2026-08-04 12:00:00", DisplayName = "Monday waits for Tuesday")]
    [DataRow("2026-08-07 08:00:00", "2026-08-11 12:00:00", DisplayName = "Friday waits for Tuesday")]
    [DataRow("2026-08-08 20:00:00", "2026-08-11 12:00:00", DisplayName = "Saturday waits for Tuesday")]
    [DataRow("2026-08-09 20:00:00", "2026-08-11 12:00:00", DisplayName = "Sunday waits for Tuesday")]
    public void CalculateNextRunTime_SchedulesNoonOnTheNextTuesdayWednesdayOrThursday(string now, string expected)
    {
        var result = service.CalculateNextRunTime(Parse(now));

        Assert.AreEqual(Parse(expected), result);
    }

    [TestMethod]
    public void CalculateNextRunTime_ResultIsAlwaysInTheFutureAndOnARunDay()
    {
        var start = Parse("2026-08-03 00:00:00");

        // Sweep every hour of a full week to make sure no input produces a run time in the past
        // or on a day the cleanup is not supposed to run.
        for (var offset = 0; offset < 24 * 7; offset++)
        {
            var now = start.AddHours(offset);
            var next = service.CalculateNextRunTime(now);

            Assert.IsGreaterThan(now, next, $"Next run time must be after {now:O}");
            Assert.AreEqual(new TimeSpan(12, 0, 0), next.TimeOfDay, $"Run time for {now:O} must be noon");
            Assert.IsTrue(
                next.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday,
                $"Run day for {now:O} was {next.DayOfWeek}");
        }
    }

    private static DateTime Parse(string value) =>
        DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
