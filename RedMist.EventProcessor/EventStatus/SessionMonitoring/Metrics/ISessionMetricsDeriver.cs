using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus.SessionMonitoring.Metrics;

/// <summary>
/// Works out the session-wide race metrics that only a Multiloop feed supplies - lead changes, laps
/// led, caution counts and the average race speed - for the events that have no Multiloop feed,
/// which is most of them.
///
/// Nothing here ever replaces a value the timing feed already provided. The check is per field, not
/// per session: a feed can supply some of these and not others, and a derived number that disagrees
/// with what the live timing screen showed during the race is worse than leaving the field null.
/// </summary>
public interface ISessionMetricsDeriver
{
    /// <summary>
    /// Fills in the flag counters and the average race speed, all of which come from the session
    /// state itself. Needs no lap log and no database, so it always runs.
    /// </summary>
    /// <param name="state">The finished session's state, updated in place.</param>
    /// <param name="eventLapDistance">
    /// <see cref="TimingCommon.Models.Configuration.Event.Distance"/> as the organizer entered it.
    /// The average race speed is left null when it is blank or unreadable.
    /// </param>
    void ApplyFlagMetrics(SessionState state, string? eventLapDistance);

    /// <summary>
    /// Fills in the metrics that need per-lap leader history - lead changes, laps led overall and
    /// in class, and the leader's green and yellow lap counts. The finishing snapshot does not carry
    /// that history, so it is read back out of the session's lap log.
    /// </summary>
    /// <returns>
    /// Whether the metrics are settled and the session needs no further attempt. False means the lap
    /// log did not yet cover the session well enough to trust - see
    /// <see cref="SessionMetricsDeriver.TryApplyLapMetrics"/> - and nothing was written.
    /// </returns>
    bool TryApplyLapMetrics(SessionState state, ISessionLapLog lapLog);
}

/// <summary>
/// The lap log for one session, as the metrics need to see it. Kept behind an interface so the
/// derivation can be exercised without a database, and so the summary can be read on its own: it is
/// a small aggregate, and it is what decides whether reading the whole log is worth doing at all.
/// </summary>
public interface ISessionLapLog
{
    /// <summary>How much of the session each car has logged.</summary>
    IReadOnlyDictionary<string, LoggedCarSummary> ReadSummary();

    /// <summary>
    /// Every logged lap, in the order the laps were completed. Enumerated lazily: a twelve hour
    /// enduro logs tens of thousands of laps, each carrying a serialized car position.
    /// </summary>
    IEnumerable<LoggedLap> ReadLaps();
}

/// <summary>What one car's rows in the lap log add up to.</summary>
/// <param name="LapCount">How many laps were logged for the car.</param>
/// <param name="HighestLapNumber">The furthest the car got, as logged.</param>
public readonly record struct LoggedCarSummary(int LapCount, int HighestLapNumber);

/// <summary>
/// One completed lap as it was logged, reduced to the fields the metrics read. The positions are
/// the ones the timing system was showing when the lap was completed, which is what makes the
/// derived numbers agree with what viewers saw at the time.
/// </summary>
public readonly record struct LoggedLap(string CarNumber, int LapNumber, Flags Flag, int OverallPosition, int ClassPosition);
