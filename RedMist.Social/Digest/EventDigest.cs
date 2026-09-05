using System.Globalization;

namespace RedMist.Social.Digest;

/// <summary>
/// The complete set of facts about one event's race results, computed deterministically from stored
/// session results. This is the only thing a post generator is allowed to draw on: it never queries
/// the database and never infers, so anything absent here must not appear in a post.
/// </summary>
/// <remarks>
/// Covers every race session of the event in one digest, because a weekend produces one post rather
/// than one per race. Practice, qualifying, driver education and test sessions are excluded upstream
/// by <see cref="EventDigestBuilder"/>.
/// </remarks>
public sealed record EventDigest(
    int EventId,
    string EventName,
    string OrganizationName,
    string TrackName,
    string? CourseConfiguration,
    DateTime StartDate,
    DateTime EndDate,
    bool IsEligibleForPublication,
    IReadOnlyList<SessionDigest> Sessions,
    DigestProvenance Provenance)
{
    /// <summary>
    /// Every value that may legitimately appear in generated copy: names, car numbers, lap counts,
    /// times and gaps. The output validator checks generated text against this set, so a number or
    /// name the model invented has nothing to match and the draft is rejected.
    /// </summary>
    /// <remarks>
    /// Deliberately generous about numbers -- it exists to catch invention, not to police phrasing.
    /// A value the model derives correctly but that is absent here (a computed margin, a converted
    /// unit) will still be flagged, which is why the validator treats a miss as "regenerate", not
    /// "discard".
    /// </remarks>
    public IReadOnlySet<string> AssertableFacts()
    {
        var facts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(facts, EventName);
        Add(facts, OrganizationName);
        Add(facts, TrackName);
        Add(facts, CourseConfiguration);

        foreach (var session in Sessions)
        {
            Add(facts, session.SessionName);
            foreach (var cls in session.Classes)
            {
                Add(facts, cls.ClassName);
                foreach (var finisher in cls.Podium)
                    AddFinisherNames(facts, finisher);
            }

            AddFinisherNames(facts, session.Highlights.FastestLapOverall);
            AddFinisherNames(facts, session.Highlights.BiggestMoverOverall);
            foreach (var finisher in session.Highlights.ClassFastestLaps)
                AddFinisherNames(facts, finisher);
        }

        foreach (var numeric in NumericFacts())
            Add(facts, numeric);

        return facts;
    }

    /// <summary>
    /// Only those facts that legitimately carry a number, for checking numeric claims in copy.
    /// </summary>
    /// <remarks>
    /// Kept separate from the name facts on purpose. Names contain incidental digits -- a class called
    /// "GP1", a session called "Saturday 8 Hour" -- and folding them into the numeric whitelist would
    /// silently pre-authorize small integers, which is most of what a results post asserts. Checking
    /// numbers against this narrower set is what gives the validator its teeth.
    /// </remarks>
    public IReadOnlyList<string> NumericFacts()
    {
        var numbers = new List<string>();

        void AddNumber(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                numbers.Add(value.Trim());
        }

        void AddInt(int value) => AddNumber(value.ToString(CultureInfo.InvariantCulture));

        void AddFinisherNumbers(Finisher? f)
        {
            if (f is null)
                return;
            AddNumber(f.CarNumber);
            AddInt(f.Position);
            AddInt(f.LapsCompleted);
            AddNumber(f.GapToLeader);
            AddNumber(f.BestLapTime);
            AddNumber(f.LapsLed?.ToString(CultureInfo.InvariantCulture));
            AddNumber(f.PositionsGained?.ToString(CultureInfo.InvariantCulture));
        }

        // Dates are assertable so copy may say when the race ran without tripping the validator.
        AddInt(StartDate.Year);
        AddInt(StartDate.Month);
        AddInt(StartDate.Day);
        AddInt(EndDate.Year);
        AddInt(EndDate.Month);
        AddInt(EndDate.Day);
        AddInt(Sessions.Count);
        AddInt(Sessions.Sum(s => s.Classes.Count));

        foreach (var session in Sessions)
        {
            AddInt(session.CarCount);
            AddInt(session.Classes.Count);
            AddNumber(session.LeadChanges?.ToString(CultureInfo.InvariantCulture));
            AddNumber(session.NumberOfYellows?.ToString(CultureInfo.InvariantCulture));
            AddNumber(session.AverageRaceSpeed);

            foreach (var cls in session.Classes)
            {
                AddInt(cls.Entries);
                foreach (var finisher in cls.Podium)
                    AddFinisherNumbers(finisher);
            }

            AddFinisherNumbers(session.Highlights.FastestLapOverall);
            AddFinisherNumbers(session.Highlights.BiggestMoverOverall);
            foreach (var finisher in session.Highlights.ClassFastestLaps)
                AddFinisherNumbers(finisher);
        }

        return numbers;
    }

    private static void AddFinisherNames(HashSet<string> facts, Finisher? finisher)
    {
        if (finisher is null)
            return;

        Add(facts, finisher.Class);
        Add(facts, finisher.Team);
        Add(facts, finisher.EntryName);
        Add(facts, finisher.LastDriverName);
        Add(facts, finisher.DisplayName);
    }

    private static void Add(HashSet<string> facts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            facts.Add(value.Trim());
    }
}

/// <summary>Results and highlights for a single race session.</summary>
public sealed record SessionDigest(
    int SessionId,
    string SessionName,
    DateTime StartUtc,
    DateTime? EndUtc,
    int CarCount,
    int? LeadChanges,
    int? NumberOfYellows,
    string? AverageRaceSpeed,
    IReadOnlyList<ClassResult> Classes,
    SessionHighlights Highlights);

/// <summary>
/// Finishing order for one class. <paramref name="Entries"/> counts every car that took part, not
/// just those on the podium, so copy can say "won a 14 car class" without inventing the field size.
/// </summary>
public sealed record ClassResult(
    string ClassName,
    int Entries,
    IReadOnlyList<Finisher> Podium);

/// <summary>
/// One car's finishing record. Positions are taken from the final stored session snapshot, so they
/// reflect the timing system's last word rather than any later adjustment made off-system.
/// </summary>
/// <param name="Position">Finishing position within the class.</param>
/// <param name="CarNumber">Car number as the timing system reported it.</param>
/// <param name="Class">Class this result is within.</param>
/// <param name="Team">Team name from the event entry list, where the organizer supplied one.</param>
/// <param name="EntryName">Entry name from the event entry list; often the driver for single-driver cars.</param>
/// <param name="LastDriverName">
/// The driver in the car at the end of the session. For an enduro with driver changes this is the
/// final stint only, never the full roster -- copy must not present it as "the" driver of the car.
/// </param>
/// <param name="GapToLeader">Gap to the class leader as the timing system formatted it, e.g. "2 laps" or "12.481".</param>
/// <param name="DisplayName">
/// The name copy should call this entry by. Resolved once here rather than left to the generator,
/// which would otherwise have to pick between three fields of wildly different reliability -- across
/// recent events the entry name is present ~97% of the time, a team name only ~25%.
/// </param>
/// <param name="NameSource">Which field <paramref name="DisplayName"/> came from, so review can see when it fell back to a bare car number.</param>
/// <param name="LapsCompleted">Laps the car completed in the session.</param>
/// <param name="BestLapTime">The car's best lap of the session, formatted by the timing system.</param>
/// <param name="LapsLed">
/// Laps led within the class. Null for sessions finalized before the pipeline began deriving it,
/// so the back catalog will stay empty here while new events fill in.
/// </param>
/// <param name="PositionsGained">
/// Places gained within the class relative to the starting grid. Null when the timing system never
/// established a starting position, rather than zero, which would read as "gained none".
/// </param>
public sealed record Finisher(
    int Position,
    string CarNumber,
    string Class,
    string? Team,
    string? EntryName,
    string? LastDriverName,
    string DisplayName,
    FinisherNameSource NameSource,
    int LapsCompleted,
    string? GapToLeader,
    string? BestLapTime,
    int? LapsLed,
    int? PositionsGained);

/// <summary>Where a finisher's <see cref="Finisher.DisplayName"/> came from.</summary>
public enum FinisherNameSource
{
    /// <summary>Team name from the entry list. Best, but present for only a minority of events.</summary>
    Team,

    /// <summary>Entry name from the entry list. The usual source.</summary>
    EntryName,

    /// <summary>Neither name was supplied; the car number is all the copy can name.</summary>
    CarNumber,
}

/// <summary>
/// Session-wide standouts. Each is precomputed by the timing pipeline rather than derived here, so a
/// null means the pipeline did not identify one -- not that the digest failed to look. Older sessions
/// are consistently sparser than recent ones, so copy must treat every one of these as optional.
/// </summary>
public sealed record SessionHighlights(
    Finisher? FastestLapOverall,
    Finisher? BiggestMoverOverall,
    IReadOnlyList<Finisher> ClassFastestLaps);

/// <summary>
/// Where a digest came from and what it could not establish.
/// </summary>
/// <param name="ComputedAtUtc">When the digest was built.</param>
/// <param name="DigestVersion">
/// Bumped whenever the projection changes. Stored with generated posts so a change in the numbers
/// can be told from a change in the wording.
/// </param>
/// <param name="SourceHash">
/// Hash of the session state this digest was computed from. Session results are rewritten in place
/// when a later, more complete snapshot arrives, so this is what reveals that the source moved under
/// a draft that was already written.
/// </param>
/// <param name="Warnings">
/// Everything the digest could not establish, in human-readable form. Surfaced in review and passed
/// to the generator so it can hedge rather than assert.
/// </param>
public sealed record DigestProvenance(
    DateTime ComputedAtUtc,
    string DigestVersion,
    string SourceHash,
    IReadOnlyList<string> Warnings);
