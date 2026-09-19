using RedMist.TimingCommon.Models;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>A racing session and the stretch of time it actually occupied.</summary>
public readonly record struct SessionWindow(int SessionId, string Name, bool IsPracticeQualifying,
    DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Works out when each racing session of an event ran.
/// </summary>
/// <remarks>
/// <para>
/// Neither field that claims to say when a session ended can be trusted on its own.
/// <c>Session.EndTime</c> is null for a session whose processor died before finalizing it, and
/// <c>Session.IsLive</c> gets stuck true when the session monitor dies - see the remarks on
/// <c>ExportsController.HasEnded</c> for the full account of both.
/// </para>
/// <para>
/// So the end is inferred instead: a session runs until whichever comes first of its own recorded
/// end, the start of the next session, or the end of the enclosing window. That is right for the
/// ordinary case and degrades to something sensible rather than to null for the broken ones.
/// </para>
/// </remarks>
public static class SessionWindowResolver
{
    /// <summary>
    /// The windows of an event's sessions, in the order they ran.
    /// </summary>
    /// <param name="sessions">The event's sessions, in any order.</param>
    /// <param name="fallbackEndUtc">
    /// When the last session has no end time of its own, the latest moment it can have run to -
    /// normally the last activity actually observed for the event.
    /// </param>
    public static List<SessionWindow> Resolve(IEnumerable<Session> sessions, DateTime fallbackEndUtc)
    {
        var ordered = sessions.OrderBy(s => s.StartTime).ThenBy(s => s.Id).ToList();
        var windows = new List<SessionWindow>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var session = ordered[i];
            var start = DateTime.SpecifyKind(session.StartTime, DateTimeKind.Utc);

            var end = session.EndTime is { } recorded
                ? DateTime.SpecifyKind(recorded, DateTimeKind.Utc)
                : i + 1 < ordered.Count
                    ? DateTime.SpecifyKind(ordered[i + 1].StartTime, DateTimeKind.Utc)
                    : fallbackEndUtc;

            // A recorded end that runs past the next session's start means one of the two is wrong.
            // Truncating keeps the windows from overlapping, which would double-count viewers.
            if (i + 1 < ordered.Count)
            {
                var next = DateTime.SpecifyKind(ordered[i + 1].StartTime, DateTimeKind.Utc);
                if (end > next)
                {
                    end = next;
                }
            }

            // A session with no duration cannot be reported on, and would divide by zero downstream.
            if (end <= start)
            {
                continue;
            }

            windows.Add(new SessionWindow(session.Id, session.Name, session.IsPracticeQualifying, start, end));
        }

        return windows;
    }
}
