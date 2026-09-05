using RedMist.Backend.Shared.Utilities;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.Social.Digest;

/// <summary>
/// Projects stored session results into an <see cref="EventDigest"/>.
/// </summary>
/// <remarks>
/// Deliberately a pure projection: it takes data already loaded and returns facts, so it can be
/// tested against real sessions without a database and reused by both the compose job and the review
/// API. Almost nothing here is calculated -- fastest lap, biggest mover, laps led and lead changes
/// are all established by the timing pipeline, and recomputing them here would create a second
/// answer that could disagree with what the live timing screen showed.
/// </remarks>
public static class EventDigestBuilder
{
    /// <summary>Bumped when the projection changes in a way that alters the facts it produces.</summary>
    public const string CurrentDigestVersion = "1";

    /// <summary>Class name used when an organizer runs an event with no class designations at all.</summary>
    private const string UnclassifiedName = "Overall";

    /// <summary>How many finishers per class the digest carries.</summary>
    private const int PodiumSize = 3;

    /// <summary>
    /// Builds the digest for one event from its stored session results.
    /// </summary>
    /// <param name="evt">The event configuration.</param>
    /// <param name="organizationName">Display name of the owning organization.</param>
    /// <param name="sessionResults">Every stored result for the event; non-race sessions are filtered out here.</param>
    /// <param name="timeProvider">Clock, for deterministic provenance timestamps in tests.</param>
    public static EventDigest Build(
        Event evt,
        string organizationName,
        IEnumerable<SessionResult> sessionResults,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(sessionResults);

        var clock = timeProvider ?? TimeProvider.System;
        var warnings = new List<string>();
        var sessions = new List<SessionDigest>();
        var hashInputs = new List<string>();

        foreach (var result in sessionResults.OrderBy(r => r.Start).ThenBy(r => r.SessionId))
        {
            var state = result.SessionState;
            if (state is null)
            {
                // Pre-SessionState rows carry only the obsolete Payload. Recent events always have
                // SessionState, so this is a back-catalog artifact rather than a live failure.
                warnings.Add($"Session {result.SessionId} has no session state and was skipped.");
                continue;
            }

            // Classified from the name rather than state.IsPracticeQualifying: that flag is written
            // once when a session is created and never backfilled, so historical sessions carry a
            // stale answer from whatever the term list said at the time.
            if (SessionHelper.IsPracticeOrQualifyingSession(state.SessionName))
                continue;

            if (state.CarPositions.Count == 0)
            {
                warnings.Add($"Race session '{state.SessionName}' ({result.SessionId}) has no car positions and was skipped.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(state.SessionName))
                warnings.Add($"Session {result.SessionId} has no name and was treated as a race.");

            sessions.Add(BuildSession(result, state, warnings));
            hashInputs.Add(JsonSerializer.Serialize(state));
        }

        if (sessions.Count == 0)
            warnings.Add("No race sessions with results were found for this event.");

        var eligible = !evt.IsPrivate && !evt.HideName && !evt.IsSimulation && !evt.IsDeleted;
        if (!eligible)
            warnings.Add("Event is private, name-hidden, a simulation or deleted, and must not be published.");

        var provenance = new DigestProvenance(
            ComputedAtUtc: clock.GetUtcNow().UtcDateTime,
            DigestVersion: CurrentDigestVersion,
            SourceHash: ComputeSourceHash(evt, hashInputs),
            Warnings: warnings);

        return new EventDigest(
            EventId: evt.Id,
            EventName: evt.Name,
            OrganizationName: organizationName,
            TrackName: evt.TrackName,
            CourseConfiguration: string.IsNullOrWhiteSpace(evt.CourseConfiguration) ? null : evt.CourseConfiguration,
            StartDate: evt.StartDate,
            EndDate: evt.EndDate,
            IsEligibleForPublication: eligible,
            Sessions: sessions,
            Provenance: provenance);
    }

    private static SessionDigest BuildSession(SessionResult result, SessionState state, List<string> warnings)
    {
        // The entry list is where team names live; car positions carry only the number. Organizers
        // occasionally repeat a number across entries, so first-wins rather than throwing.
        var entriesByNumber = new Dictionary<string, EventEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.EventEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Number))
                entriesByNumber.TryAdd(entry.Number, entry);
        }

        var classes = new List<ClassResult>();
        var grouped = state.CarPositions
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Class) ? string.Empty : c.Class.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var className = group.Key;
            if (className.Length == 0)
            {
                className = UnclassifiedName;
                warnings.Add($"Session '{state.SessionName}' has cars with no class; they are grouped as '{UnclassifiedName}'.");
            }

            var podium = group
                .Where(c => c.ClassPosition > 0)
                .OrderBy(c => c.ClassPosition)
                .Take(PodiumSize)
                .Select(c => ToFinisher(c, className, entriesByNumber))
                .ToList();

            if (podium.Count == 0)
            {
                warnings.Add($"Class '{className}' in session '{state.SessionName}' has no finishing positions and was skipped.");
                continue;
            }

            // Positions are assigned 1..N per class upstream, but under a stricter comparer than the
            // grouping here: "GP1" and "gp1 " are two independent sequences there and one class here,
            // which merges two P1s into a single podium. Cheap to detect, and the alternative is
            // publishing two winners of the same class.
            var duplicated = podium.GroupBy(f => f.Position).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicated.Count > 0)
            {
                warnings.Add(
                    $"Class '{className}' in session '{state.SessionName}' has more than one car at " +
                    $"position {string.Join(", ", duplicated)}; the class name may be inconsistently cased or spaced.");
            }

            // The winner is what a results post is built around, so being unable to name it is worth
            // flagging before a draft is written rather than after it reads oddly.
            if (podium[0].NameSource == FinisherNameSource.CarNumber)
            {
                warnings.Add(
                    $"Winner of '{className}' in session '{state.SessionName}' has no team or entry name; " +
                    $"only car #{podium[0].CarNumber} is available.");
            }

            classes.Add(new ClassResult(className, group.Count(), podium));
        }

        return new SessionDigest(
            SessionId: result.SessionId,
            SessionName: state.SessionName,
            StartUtc: result.Start,
            EndUtc: state.SessionEndTime,
            CarCount: state.CarPositions.Count,
            LeadChanges: state.LeadChanges,
            NumberOfYellows: state.NumberOfYellows,
            AverageRaceSpeed: string.IsNullOrWhiteSpace(state.AverageRaceSpeed) ? null : state.AverageRaceSpeed,
            Classes: classes,
            Highlights: BuildHighlights(state, entriesByNumber));
    }

    private static SessionHighlights BuildHighlights(SessionState state, Dictionary<string, EventEntry> entriesByNumber)
    {
        Finisher? Convert(CarPosition? car) =>
            car is null ? null : ToFinisher(car, ClassNameOf(car), entriesByNumber);

        var classFastest = state.CarPositions
            .Where(c => c.IsBestTimeClass)
            .Select(c => ToFinisher(c, ClassNameOf(c), entriesByNumber))
            .ToList();

        return new SessionHighlights(
            FastestLapOverall: Convert(state.CarPositions.FirstOrDefault(c => c.IsBestTime)),
            BiggestMoverOverall: Convert(state.CarPositions.FirstOrDefault(c => c.IsOverallMostPositionsGained)),
            ClassFastestLaps: classFastest);
    }

    private static string ClassNameOf(CarPosition car) =>
        string.IsNullOrWhiteSpace(car.Class) ? UnclassifiedName : car.Class.Trim();

    private static Finisher ToFinisher(CarPosition car, string className, Dictionary<string, EventEntry> entriesByNumber)
    {
        EventEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(car.Number))
            entriesByNumber.TryGetValue(car.Number, out entry);

        var team = Clean(entry?.Team);
        var entryName = Clean(entry?.Name);
        var carNumber = car.Number ?? string.Empty;

        // Resolved here so copy never has to choose between three fields of very different
        // reliability, nor invent a name when the organizer supplied none.
        var (displayName, nameSource) = team is not null
            ? (team, FinisherNameSource.Team)
            : entryName is not null
                ? (entryName, FinisherNameSource.EntryName)
                : ($"car #{carNumber}", FinisherNameSource.CarNumber);

        return new Finisher(
            Position: car.ClassPosition,
            CarNumber: carNumber,
            Class: className,
            Team: team,
            EntryName: entryName,
            LastDriverName: Clean(car.DriverName),
            DisplayName: displayName,
            NameSource: nameSource,
            LapsCompleted: car.LastLapCompleted,
            // InClassDifference, not InClassGap: Gap is the interval to the NEXT car in class, while
            // Difference is the interval to the class leader. Using Gap here would be right for P2 by
            // coincidence and wrong for every finisher below it.
            GapToLeader: Clean(car.InClassDifference),
            BestLapTime: Clean(car.BestTime),
            LapsLed: car.LapsLedInClass,
            // Only gains. A negative value is a car that lost places, which no results post reports,
            // and exposing it would let copy say "gained 5" about a car that dropped five.
            PositionsGained: car.InClassPositionsGained > 0
                ? car.InClassPositionsGained
                : null);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Hashes the session state AND the event fields the digest depends on, so that a later rewrite
    /// of the stored result -- or the organizer marking the event private, renaming it or deleting it
    /// -- can be detected against a draft that has already been generated. Covering only the sessions
    /// would let an event withdrawn after a draft was written still look unchanged.
    /// </summary>
    /// <summary>
    /// Separator placed between serialized states so two sessions cannot concatenate into the
    /// same bytes as a differently-split pair. Never appears in JSON text.
    /// </summary>
    private const char UnitSeparator = '\u001f';

    private static string ComputeSourceHash(Event evt, IReadOnlyList<string> serializedStates)
    {
        if (serializedStates.Count == 0)
            return string.Empty;

        var eventFingerprint = string.Join('|',
            evt.Id, evt.Name, evt.TrackName, evt.CourseConfiguration,
            evt.StartDate.ToString("O", CultureInfo.InvariantCulture),
            evt.EndDate.ToString("O", CultureInfo.InvariantCulture),
            evt.IsPrivate, evt.HideName, evt.IsSimulation, evt.IsDeleted);
        serializedStates = [eventFingerprint, .. serializedStates];

        var combined = Encoding.UTF8.GetBytes(string.Join(UnitSeparator, serializedStates));
        return Convert.ToHexStringLower(SHA256.HashData(combined));
    }
}
