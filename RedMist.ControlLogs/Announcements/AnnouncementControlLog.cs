using RedMist.TimingCommon.Models;
using System.Text.RegularExpressions;

namespace RedMist.ControlLogs.Announcements;

/// <summary>
/// <see cref="IControlLog"/> backed by race-control announcements from the external timing source.
/// Unlike the Google Sheets sources this does not fetch anything on load — the announcements arrive on a
/// Redis stream and are parsed into <see cref="IAnnouncementControlLogStore"/> by the stream consumer;
/// <see cref="LoadControlLogAsync"/> just returns the current snapshot so the existing poll/diff/publish
/// pipeline (<c>ControlLogCache</c> / <c>StatusAggregatorService</c>) needs no changes.
/// </summary>
public class AnnouncementControlLog(IAnnouncementControlLogStore store) : IControlLog
{
    private readonly IAnnouncementControlLogStore store = store;

    public string Type => ControlLogType.ANNOUNCEMENT;

    /// <inheritdoc />
    public Regex WarningPattern { get; } = new(@".*Warning.*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    // Matches the AKS penalty phrasing: "1 Lap Penalty", "2 Laps", "2 LapPenalty", "5 Lap Penalty".
    public Regex LapPenaltyPattern { get; } = new(@"(\d+)\s+Lap", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    // The external source has no disciplinary drive-through/black-flag penalty text (a "MECHANICAL BLACK
    // FLAG" is a mechanical call, not a penalty), so this intentionally matches nothing in this feed.
    public Regex BlackFlagPattern { get; } = new(@".*Drive Through Penalty.*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<(bool success, IEnumerable<ControlLogEntry> logs)> LoadControlLogAsync(string parameter, CancellationToken stoppingToken = default)
        => Task.FromResult((true, (IEnumerable<ControlLogEntry>)store.Get()));

    public void Dispose() => GC.SuppressFinalize(this);
}
