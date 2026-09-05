using RedMist.Backend.Shared.Utilities;
using RedMist.TimingCommon.Models;
using System.Globalization;

namespace RedMist.EventProcessor.EventStatus.SessionMonitoring.Metrics;

/// <summary>
/// <see cref="ISessionMetricsDeriver"/> over a finished session's own state and lap log.
///
/// What counts as a caution is the one judgement here worth stating plainly, because these numbers
/// have to mean the same thing whether they came from Multiloop or from this class:
///
/// <list type="bullet">
/// <item><see cref="Flags.WavingYellow"/> is a corner worker warning the cars about something at
/// their own station. The rest of the track is still racing, so it is not a caution.</item>
/// <item><see cref="Flags.Purple35"/> and <see cref="Flags.Purple60"/> are, despite being a speed
/// limit rather than a flag colour. The whole field is neutralized to a pace speed, which is the
/// same event Multiloop reports as a yellow. They have to be counted, because they routinely stand
/// in for the yellow rather than accompanying it: the flag processor drops a caution whose start
/// time a purple override has taken over, so counting yellows alone reports races that ran six and
/// eight full-course cautions as having had none.</item>
/// </list>
/// </summary>
public class SessionMetricsDeriver : ISessionMetricsDeriver
{
    /// <summary>
    /// A derived speed outside this band is thrown away rather than written. The organizer types the
    /// lap distance in by hand and it is not always in miles - one production event carries "238" -
    /// so a plainly wrong number is an expected failure, not a rare one.
    /// </summary>
    private const double MinPlausibleMph = 5;
    private const double MaxPlausibleMph = 200;

    /// <summary>
    /// The share of the leader's own laps that has to be in the lap log before laps led and lead
    /// changes are believed. See <see cref="TryApplyLapMetrics"/>.
    ///
    /// Not all of them: a handful of production sessions are two or three rows short of a race
    /// several hundred laps long, and throwing a race's whole story away over a tenth of a percent
    /// would be its own kind of wrong. Below this the gaps stop being a handful - the next sessions
    /// down are missing eight and twenty four laps - and the numbers stop being worth writing.
    /// </summary>
    private const double MinLoggedShareOfLeaderLaps = 0.95;

    /// <summary>
    /// The share of the whole field's laps that has to be in the log. Where the leader's share is
    /// about how far the numbers can be trusted, this is about whether the log belongs to this
    /// session at all: the sessions it rejects hold about one lap per car.
    /// </summary>
    private const double MinLoggedShareOfLaps = 0.5;

    /// <summary>
    /// A flag that was flown, with the period it covered. Consecutive entries for the same flag are
    /// already merged, and the period is always closed.
    /// </summary>
    private readonly record struct FlagPeriod(Flags Flag, DateTime Start, DateTime End);


    public void ApplyFlagMetrics(SessionState state, string? eventLapDistance)
    {
        ArgumentNullException.ThrowIfNull(state);

        var periods = CollapseFlagPeriods(state.FlagDurations, SessionEnd(state));
        if (periods.Count > 0)
        {
            // Yellow here means any full-course caution: the periods come back with the purple
            // speed limits already folded into it. See the note on the class.
            state.NumberOfYellows ??= periods.Count(p => p.Flag == Flags.Yellow);
            state.GreenTimeMs ??= TotalMilliseconds(periods, Flags.Green);
            state.YellowTimeMs ??= TotalMilliseconds(periods, Flags.Yellow);
            state.RedTimeMs ??= TotalMilliseconds(periods, Flags.Red);
        }

        // Multiloop leaves this empty rather than absent when it has nothing, so blank counts as
        // "not supplied" here.
        if (string.IsNullOrWhiteSpace(state.AverageRaceSpeed))
        {
            var speed = DeriveAverageRaceSpeedMph(state, eventLapDistance);
            if (speed is not null)
                state.AverageRaceSpeed = speed;
        }
    }

    /// <summary>
    /// <inheritdoc cref="ISessionMetricsDeriver.TryApplyLapMetrics"/>
    ///
    /// The lap log is written by a different service reading a Redis stream asynchronously, so at
    /// the moment a session is finalized its tail may not have reached the database yet. Deriving
    /// from a short log would undercount the end of the race - the part that decides the winner - so
    /// the log is measured against the finishing snapshot first and nothing is written unless it
    /// covers the session:
    ///
    /// <list type="bullet">
    /// <item>the leader's last lap has to be in the log, which is what catches a missing tail;</item>
    /// <item>the leader has to have nearly as many rows as it ran laps, which is what catches a hole
    /// in the middle - the logger reads its stream without ever reclaiming what it did not
    /// acknowledge, so an outage loses that window outright and the tail then arrives looking
    /// perfectly healthy; and</item>
    /// <item>the log has to hold a credible share of the field's laps, which is what catches a
    /// session whose cars were carried over from another one - the scratch run a timing system
    /// announces at every run change takes a copy of the outgoing session's field but has a lap log
    /// of its own that holds only a lap or so per car.</item>
    /// </list>
    /// </summary>
    public bool TryApplyLapMetrics(SessionState state, ISessionLapLog lapLog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(lapLog);

        // Laps led are only ever written as a set. A feed that supplied them for some cars and not
        // others still supplied them, and filling the gaps from here would put two different
        // measurements in one column.
        var lapsLedSupplied = state.CarPositions.Any(c => c.LapsLedOverall is not null || c.LapsLedInClass is not null);
        if (lapsLedSupplied && state.LeadChanges is not null && state.GreenLaps is not null && state.YellowLaps is not null)
            return true;

        var totalLaps = state.CarPositions.Sum(c => (long)Math.Max(0, c.LastLapCompleted));
        if (totalLaps == 0)
            return true; // Nobody completed a lap. Nothing to derive, and nothing to wait for.

        var leader = Leader(state);
        if (leader?.Number is null)
            return true; // No car to measure the log against, so no later attempt would do better.

        var summary = lapLog.ReadSummary();
        if (!summary.TryGetValue(leader.Number, out var leaderLog)
            || leaderLog.HighestLapNumber < leader.LastLapCompleted
            || leaderLog.LapCount < leader.LastLapCompleted * MinLoggedShareOfLeaderLaps)
        {
            return false;
        }

        var loggedLaps = summary.Values.Sum(s => (long)s.LapCount);
        if (loggedLaps < totalLaps * MinLoggedShareOfLaps)
            return false;

        var walk = WalkLeaderboard(lapLog.ReadLaps());

        // Nothing is written off the back of a walk that found nothing to measure. A log whose
        // positions never resolved would otherwise be reported as a race with no leader and no laps
        // led by anyone, which reads as fact rather than as the absence of one.
        if (walk.LeaderLaps == 0)
            return true;

        state.LeadChanges ??= walk.LeadChanges;

        // Likewise the flag against each lap: it is copied from the session's current flag, which is
        // Unknown until the first flag arrives, so a log that never saw one has no green laps to
        // report rather than none to have run.
        if (walk.FlaggedLeaderLaps > 0)
        {
            state.GreenLaps ??= walk.GreenLaps;
            state.YellowLaps ??= walk.YellowLaps;
        }

        if (!lapsLedSupplied)
        {
            foreach (var car in state.CarPositions)
            {
                if (string.IsNullOrEmpty(car.Number))
                    continue;

                // Zero rather than null for a car that never led: it ran, and it led nothing.
                car.LapsLedOverall = walk.LapsLedOverall.GetValueOrDefault(car.Number);
                if (walk.ClassLeaderLaps > 0)
                    car.LapsLedInClass = walk.LapsLedInClass.GetValueOrDefault(car.Number);
            }
        }

        return true;
    }

    #region Lap log

    /// <summary>
    /// What one pass over the lap log adds up to.
    ///
    /// The three counts of what the walk actually saw are there to tell an empty answer from an
    /// absent one. LeaderLaps and ClassLeaderLaps zero means the log carried no positions worth
    /// reading, which is not the same as a race nobody led; FlaggedLeaderLaps zero means its flags
    /// never resolved, which is not the same as a race run under no flag.
    /// </summary>
    private sealed record LeaderboardWalk(
        int LeadChanges,
        int GreenLaps,
        int YellowLaps,
        int LeaderLaps,
        int ClassLeaderLaps,
        int FlaggedLeaderLaps,
        Dictionary<string, int> LapsLedOverall,
        Dictionary<string, int> LapsLedInClass);

    /// <summary>
    /// Counts laps led and lead changes from the positions the timing system recorded against each
    /// completed lap.
    ///
    /// A lead change is counted whenever the car holding first place is not the one that held it at
    /// the previous lap completed at the front, so a leader who pits and takes the place straight
    /// back counts twice - which is how lead changes are counted in racing.
    ///
    /// Green and yellow laps are the leader's rather than the field's: they answer how much of the
    /// race was run under green, which is a property of the race and not of any one car, and that is
    /// what Multiloop reports.
    /// </summary>
    private static LeaderboardWalk WalkLeaderboard(IEnumerable<LoggedLap> laps)
    {
        string? leader = null;
        int leadChanges = 0, greenLaps = 0, yellowLaps = 0;
        int leaderLaps = 0, classLeaderLaps = 0, flaggedLeaderLaps = 0;
        Dictionary<string, int> ledOverall = [], ledInClass = [];

        foreach (var lap in laps)
        {
            if (string.IsNullOrEmpty(lap.CarNumber))
                continue;

            if (lap.ClassPosition == 1)
            {
                ledInClass[lap.CarNumber] = ledInClass.GetValueOrDefault(lap.CarNumber) + 1;
                classLeaderLaps++;
            }

            if (lap.OverallPosition != 1)
                continue;

            ledOverall[lap.CarNumber] = ledOverall.GetValueOrDefault(lap.CarNumber) + 1;
            leaderLaps++;

            if (lap.Flag != Flags.Unknown)
                flaggedLeaderLaps++;

            if (lap.Flag == Flags.Green)
                greenLaps++;
            else if (AsCaution(lap.Flag))
                yellowLaps++;

            if (leader is not null && leader != lap.CarNumber)
                leadChanges++;
            leader = lap.CarNumber;
        }

        return new LeaderboardWalk(leadChanges, greenLaps, yellowLaps, leaderLaps, classLeaderLaps,
            flaggedLeaderLaps, ledOverall, ledInClass);
    }

    /// <summary>
    /// The car whose laps stand for the race distance. Falls back to whoever completed the most laps
    /// for a session that ended without the timing system settling on a first place.
    /// </summary>
    private static CarPosition? Leader(SessionState state)
    {
        var numbered = state.CarPositions.Where(c => !string.IsNullOrEmpty(c.Number)).ToList();
        return numbered.FirstOrDefault(c => c.OverallPosition == 1) ?? numbered.MaxBy(c => c.LastLapCompleted);
    }

    #endregion

    #region Flag periods

    /// <summary>
    /// The session's flags as ordered, closed periods.
    ///
    /// The entries are not stored in order and the same flag can be recorded twice in a row, so they
    /// are sorted and consecutive entries of one flag merged - otherwise a single caution written as
    /// two adjacent yellow entries would be counted as two cautions.
    /// </summary>
    private static List<FlagPeriod> CollapseFlagPeriods(IEnumerable<FlagDuration> durations, DateTime sessionEnd)
    {
        var periods = new List<FlagPeriod>();

        foreach (var duration in durations.Where(d => d is not null).OrderBy(d => d.StartTime))
        {
            // The last flag of a session is still open when it is written out - the checkered flag
            // is never followed by another - so it is closed at the end of the session rather than
            // dropped or taken as instantaneous.
            var end = duration.EndTime ?? sessionEnd;
            if (end < duration.StartTime)
                end = duration.StartTime;

            var flag = AsCaution(duration.Flag) ? Flags.Yellow : duration.Flag;

            if (periods.Count > 0 && periods[^1].Flag == flag)
            {
                if (end > periods[^1].End)
                    periods[^1] = periods[^1] with { End = end };
            }
            else
            {
                periods.Add(new FlagPeriod(flag, duration.StartTime, end));
            }
        }

        return periods;
    }

    /// <summary>
    /// Whether a flag means the whole field is neutralized. A caution that starts under yellow and
    /// runs on under a purple speed limit is one caution, not two, so they collapse together. See
    /// the note on the class.
    /// </summary>
    private static bool AsCaution(Flags flag) => flag is Flags.Yellow or Flags.Purple35 or Flags.Purple60;

    private static int TotalMilliseconds(List<FlagPeriod> periods, Flags flag)
    {
        double total = 0;
        foreach (var period in periods)
        {
            if (period.Flag == flag)
                total += (period.End - period.Start).TotalMilliseconds;
        }

        // The field is an int where Multiloop's own is a uint, so a session long enough to overflow
        // is reported as the largest value the field can hold rather than wrapping negative.
        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// When the session stopped, for closing its final flag period.
    ///
    /// <see cref="SessionState.SessionEndTime"/> is the answer when it has one, but in practice a
    /// finished session does not carry it, so how long the session ran stands in: the flag log starts
    /// when the session does, so its first entry plus the elapsed time is the end. Failing that, the
    /// last timestamp anywhere in the log is used, which closes the final period at zero length -
    /// the honest answer when there is nothing to measure it against.
    /// </summary>
    private static DateTime SessionEnd(SessionState state)
    {
        DateTime first = DateTime.MaxValue, last = DateTime.MinValue;
        foreach (var duration in state.FlagDurations)
        {
            if (duration is null)
                continue;
            if (duration.StartTime < first)
                first = duration.StartTime;
            if (duration.StartTime > last)
                last = duration.StartTime;
            if (duration.EndTime is { } end && end > last)
                last = end;
        }

        if (state.SessionEndTime is { } ended && ended != default)
            return ended;

        if (first == DateTime.MaxValue)
            return default;

        if (SessionElapsed(state) is { } elapsed)
        {
            var elapsedEnd = first + elapsed;
            if (elapsedEnd > last)
                return elapsedEnd;
        }

        return last;
    }

    /// <summary>
    /// How long the session ran.
    ///
    /// The winner's own total time is what answers this, rather than the session's running race
    /// clock. Both are the timing system's, but the race clock keeps counting after a session stops
    /// being fed and is only stopped when the session is finalized, which can be hours later - one
    /// production session reports eleven and a half hours of race clock for a six hour race that had
    /// already been over for five of them. A car's total time stops when the car does, so the
    /// leader's is the race, and it is the same car whose distance the speed is measured over.
    ///
    /// The race clock is the fallback, for a session whose leader has no total time recorded.
    /// </summary>
    private static TimeSpan? SessionElapsed(SessionState state)
    {
        if (RaceTimeParser.TryParse(Leader(state)?.TotalTime, out var leaderTime) && leaderTime > TimeSpan.Zero)
            return leaderTime;

        if (RaceTimeParser.TryParse(state.RunningRaceTime, out var raceClock) && raceClock > TimeSpan.Zero)
            return raceClock;

        return null;
    }

    #endregion

    #region Average race speed

    /// <summary>
    /// The winner's distance covered over the session's elapsed time, in mph, formatted the way the
    /// Multiloop feed formats it so consumers see one format.
    ///
    /// Elapsed time is the whole session - green, caution and red - which is what average race speed
    /// conventionally means and what Multiloop reports. Returns null unless every input is there and
    /// the answer is plausible; there is no fallback for the lap distance, because the only other
    /// lengths on hand are learned track maps and those are not reliably right.
    /// </summary>
    private static string? DeriveAverageRaceSpeedMph(SessionState state, string? eventLapDistance)
    {
        if (ParseLapDistanceMiles(eventLapDistance) is not { } lapMiles)
            return null;

        if (SessionElapsed(state) is not { } elapsed)
            return null;

        var leader = Leader(state);
        if (leader is null || leader.LastLapCompleted <= 0)
            return null;

        var mph = leader.LastLapCompleted * lapMiles / elapsed.TotalHours;
        if (double.IsNaN(mph) || mph < MinPlausibleMph || mph > MaxPlausibleMph)
            return null;

        return mph.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The lap length in miles from the free-text distance the organizer entered on the event.
    ///
    /// Every value seen in production is in miles and about a fifth carry a unit - "1.474 mi",
    /// "2.87 miles" - so a trailing unit is ignored. A value that is not in miles at all cannot be
    /// told apart here and is left to the plausibility check on the speed itself.
    ///
    /// Only a trailing unit is ignored, though. Stopping at the first character that is not part of
    /// a number would read a comma decimal separator as the end of the value and turn "1,474" into
    /// one mile - a number wrong by a third that would still pass the plausibility check.
    /// </summary>
    private static double? ParseLapDistanceMiles(string? distance)
    {
        if (string.IsNullOrWhiteSpace(distance))
            return null;

        var span = distance.AsSpan().Trim();
        var length = 0;
        while (length < span.Length && (char.IsAsciiDigit(span[length]) || span[length] == '.'))
            length++;

        // Whatever follows the number has to read as a unit rather than as more of the number.
        if (length < span.Length && !char.IsWhiteSpace(span[length]) && !char.IsAsciiLetter(span[length]))
            return null;

        if (length == 0 || !double.TryParse(span[..length], NumberStyles.Float, CultureInfo.InvariantCulture, out var miles))
            return null;

        return miles > 0 ? miles : null;
    }

    #endregion
}
