using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;
using RedMist.StatusApi.Filters;
using RedMist.StatusApi.Services.Exports;
using System.Text;

namespace RedMist.StatusApi.Controllers.V1;

/// <summary>
/// Downloadable reports built from a completed session's stored lap data.
/// </summary>
/// <remarks>
/// <para>
/// Routes:
/// <list type="bullet">
/// <item>v1/Exports/[action] - Versioned route</item>
/// <item>Exports/[action] - Legacy unversioned route (for backward compatibility)</item>
/// </list>
/// </para>
/// <para>
/// Access matches the rest of a session's data: anonymous is allowed, and private events are gated
/// by the same access code header the timing endpoints use. An export exposes nothing a caller
/// could not already page through one lap at a time - it just exposes it in one request, which is
/// why the whole controller sits behind its own rate limit and a process-wide concurrency cap.
/// </para>
/// <para>
/// Only sessions that have ended can be exported - which means having an end time, or no longer
/// being flagged live, since neither field alone is a reliable answer. A session still running has
/// lap rows arriving, so an export of one is a partial snapshot that looks, to whoever opens the
/// file later, like a complete record of the race.
/// </para>
/// </remarks>
[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")] // Also handle legacy unversioned routes
[ApiVersion("1.0")]
[ApiController]
[Authorize]
[EnableRateLimiting("exports")]
public class ExportsController : ControllerBase
{
    /// <summary>
    /// Rows a JSON or CSV lap export will stream before it stops and marks itself truncated. Sized
    /// well above a real endurance session - roughly 60 cars running for 24 hours - so that hitting
    /// it means something has gone wrong rather than that the race was long.
    /// </summary>
    public const int MaxLapRows = 200_000;

    /// <summary>
    /// Rows a lap PDF will contain. Far lower than the streamed formats because a PDF's layout needs
    /// every row in memory at once - QuestPDF composes the document twice to resolve the page count -
    /// and this pod has a 380Mi limit it shares with the live SignalR hub. It is also simply the
    /// point past which a report stops being one: anyone wanting a whole endurance session wants the
    /// CSV, and the PDF says on its front page that it stopped short.
    /// </summary>
    public const int MaxPdfLapRows = 2_000;

    /// <summary>Lap rows the pit stop scan will read before it gives up on the session.</summary>
    public const int MaxPitStopScanLaps = 200_000;

    /// <summary>Derived stops a JSON or CSV pit report will contain.</summary>
    public const int MaxPitStopRows = 20_000;

    /// <summary>Derived stops a pit report PDF will contain.</summary>
    public const int MaxPdfPitStopRows = 2_000;

    /// <summary>
    /// How many of each car's opening laps the Flagtronics driver probe looks at. Equipment that is
    /// fitted for a session reports from the moment the car takes to the track, so a session with no
    /// driver identification in any car's first laps does not have it at all.
    /// </summary>
    public const int DriverProbeLapDepth = 25;

    /// <summary>
    /// How many of each car's laps the pit entry probe looks at. Deeper than the driver probe because
    /// a first stop comes after a stint, not after an out lap, and a stint is 20 to 40 laps - but
    /// still bounded, so that a Flagtronics session in which nobody ever pitted cannot turn an
    /// availability check into a scan of the whole session.
    /// </summary>
    public const int PitProbeLapDepth = 120;

    /// <summary>
    /// Marks a lap row as carrying a Flagtronics driver identification. The stored payload uses the
    /// short JSON names from <c>CarPosition</c>, so this is <c>DriverSource</c>.
    /// </summary>
    private const string DriverSourceMarker = "\"ds\":\"";

    /// <summary>
    /// The driver source the equipment reports when it is present but has not identified anyone.
    /// A session of these has Flagtronics hardware and no drivers, which is not a report.
    /// </summary>
    private const string DriverSourceNoneMarker = "\"ds\":\"none\"";

    /// <summary>
    /// Marks a lap row as carrying a pit entry time, i.e. <c>PitEntryTime</c>. A null is serialized
    /// as <c>"pet":null</c>, so requiring the opening quote of a value is what distinguishes a row
    /// that has one.
    /// </summary>
    private const string PitEntryTimeMarker = "\"pet\":\"";

    /// <summary>
    /// Cache key for a finished session availability answer. Only sessions that have ended and are
    /// no longer flagged live are cached: their answer cannot change again except by the event being
    /// archived, which is what the expiration covers.
    /// </summary>
    private const string AVAILABILITY_KEY = "exports-availability:{0}:{1}"; // {0} = eventId, {1} = sessionId

    private static readonly HybridCacheEntryOptions availabilityCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly ExportConcurrencyLimiter limiter;
    private readonly StagedExportTracker stagedExports;
    private readonly HybridCache hcache;

    /// <summary>Logger for this controller.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportsController"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    /// <param name="tsContext">Database context factory for timing and scoring data.</param>
    /// <param name="limiter">Process-wide cap on how many exports are generated at once.</param>
    /// <param name="stagedExports">Process-wide cap on generated exports waiting to be downloaded.</param>
    /// <param name="hcache">Hybrid cache, used for the availability answer.</param>
    public ExportsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        ExportConcurrencyLimiter limiter, StagedExportTracker stagedExports, HybridCache hcache)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.limiter = limiter;
        this.stagedExports = stagedExports;
        this.hcache = hcache;
    }

    /// <summary>
    /// Reports which exports are worth offering for a session, and for which cars.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <returns>The availability flags and the session's car numbers.</returns>
    /// <response code="200">Returns the availability. An unknown event or session reports everything unavailable rather than failing.</response>
    /// <response code="401">The event is private and the access code was missing or wrong.</response>
    /// <response code="429">Too many export requests from this caller.</response>
    /// <remarks>
    /// <para>
    /// A session counts as finished when it has an <c>EndTime</c> <em>or</em> its <c>IsLive</c> flag
    /// is clear - see <see cref="HasEnded"/> for why neither field can be trusted alone. A session
    /// that is neither returns immediately: no export can be produced from one - every file endpoint
    /// answers 404 - so paying for the lap probes to describe it would be work for an answer nobody
    /// can act on. It therefore reports every flag false and no car numbers; the real answer appears
    /// once the session ends.
    /// </para>
    /// <para>
    /// A finished session answer is cached, because it cannot change again except by the event being
    /// archived - which is also why the entry expires rather than living forever. The exception is a
    /// session that has an end time and is still flagged live, which may have been picked up again
    /// and may still be gaining laps; that answer is read fresh, and any entry cached before the
    /// session was picked up is dropped so it cannot come back once the flag clears again. The file
    /// endpoints re-check against the database regardless, so a stale "available" costs a 404, never
    /// a bad file.
    /// </para>
    /// <para>
    /// It does not take a slot from the export concurrency limit; only the endpoints that actually
    /// build a file do.
    /// </para>
    /// </remarks>
    [AllowAnonymous]
    [RequireEventAccessCode]
    [EnableRateLimiting("exports-availability")]
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<ExportAvailability>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ExportAvailability>> GetAvailability(int eventId, int sessionId)
    {
        var cancellationToken = HttpContext.RequestAborted;
        Logger.LogTrace("{m} for event {eventId}, session {sessionId}", nameof(GetAvailability), eventId, sessionId);

        bool ended;
        bool stillFlaggedLive;
        using (var db = await tsContext.CreateDbContextAsync(cancellationToken))
        {
            // A single primary-key lookup, reading only what decides whether the expensive part is
            // worth doing at all.
            var session = await db.Sessions.AsNoTracking()
                .Where(s => s.EventId == eventId && s.Id == sessionId)
                .Select(s => new { s.EndTime, s.IsLive })
                .FirstOrDefaultAsync(cancellationToken);
            ended = session != null && HasEnded(session.EndTime, session.IsLive);
            stillFlaggedLive = session?.EndTime != null && session.IsLive;
        }

        if (!ended)
            return Ok(new ExportAvailability { SessionCompleted = false });

        var key = string.Format(AVAILABILITY_KEY, eventId, sessionId);

        // A session carrying an end time while still flagged live has ended at least once and may
        // have been picked up again, so its lap data can still be growing. It is served - that is the
        // whole point of not requiring the flag to be clear - but read fresh, because caching is the
        // one part of this that would keep showing a stale answer after more laps arrived.
        //
        // The entry is dropped rather than merely bypassed. Bypassing protects only while the flag is
        // set: a session ends cleanly and is cached, is picked up again, then ends again - and that
        // third call finds the flag clear and serves the pre-resume answer for the rest of the entry
        // lifetime. A session that first ended with no cars would go on reporting no lap data long
        // after the real race had run.
        if (stillFlaggedLive)
        {
            await hcache.RemoveAsync(key, cancellationToken);
            return Ok(await LoadAvailabilityFromDbAsync(eventId, sessionId, cancellationToken));
        }

        var availability = await hcache.GetOrCreateAsync(key,
            async cancel => await LoadAvailabilityFromDbAsync(eventId, sessionId, cancel),
            availabilityCacheOptions,
            cancellationToken: cancellationToken);

        return Ok(availability);
    }

    /// <summary>
    /// Reads a completed session availability answer from the database. Only reached on a cache miss.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>The availability.</returns>
    private async Task<ExportAvailability> LoadAvailabilityFromDbAsync(int eventId, int sessionId,
        CancellationToken cancellationToken)
    {
        var availability = new ExportAvailability { SessionCompleted = true };
        using var db = await tsContext.CreateDbContextAsync(cancellationToken);

        var laps = ExportDataSource.LapFilter(db, eventId, sessionId, null);
        availability.LapDataAvailable = await laps.AnyAsync(cancellationToken);
        if (!availability.LapDataAvailable)
            return availability;

        // Index-only: CarNumber is a key column of the event/session lap index, so the DISTINCT never
        // touches the heap or the stored payloads.
        var carNumbers = await laps.Select(l => l.CarNumber).Distinct().ToListAsync(cancellationToken);
        carNumbers.Sort(CarNumberComparer.Instance);
        availability.CarNumbers = carNumbers;
        availability.PitReportAvailable = await HasFlagtronicsPitDataAsync(db, eventId, sessionId, cancellationToken);

        return availability;
    }

    /// <summary>
    /// Downloads a session's lap data for one car, or for every car, as JSON, CSV or PDF.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="carNumber">The car to export. Omit or leave empty for every car in the session.</param>
    /// <param name="format">One of <c>json</c>, <c>csv</c> or <c>pdf</c>. Defaults to <c>json</c>.</param>
    /// <returns>The generated file as an attachment.</returns>
    /// <response code="200">Returns the file. Content type is application/json, text/csv or application/pdf.</response>
    /// <response code="400">The requested format is not one of json, csv or pdf.</response>
    /// <response code="401">The event is private and the access code was missing or wrong.</response>
    /// <response code="404">The session is unknown, has not ended, or has no lap data for the requested car.</response>
    /// <response code="429">Too many export requests from this caller.</response>
    /// <response code="503">This replica is already generating its maximum number of exports; retry after the interval in the Retry-After header.</response>
    /// <remarks>
    /// JSON carries every field the system recorded for each lap, wrapped in an envelope naming the
    /// event and session. The rows are re-encoded on the way out - indented, and escaped only where
    /// JSON requires it - so the file is readable in a text editor. CSV and PDF are narrower projections meant for a
    /// spreadsheet and for reading respectively. The PDF is capped far lower than the other two - see
    /// the response body's truncation marker.
    /// </remarks>
    [AllowAnonymous]
    [RequireEventAccessCode]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetCarLaps(int eventId, int sessionId, string? carNumber = null, string? format = null)
    {
        var cancellationToken = HttpContext.RequestAborted;

        if (!ExportFormats.TryParse(format, out var exportFormat))
            return InvalidFormat(format);

        carNumber = string.IsNullOrWhiteSpace(carNumber) ? null : carNumber.Trim();
        Logger.LogInformation("{m} for event {eventId}, session {sessionId}, car {car}, format {format}",
            nameof(GetCarLaps), eventId, sessionId, carNumber ?? "(all)", exportFormat);

        ExportContext context;
        using (var db = await tsContext.CreateDbContextAsync(cancellationToken))
        {
            var session = await LoadCompletedSessionAsync(db, eventId, sessionId, cancellationToken);
            if (session == null)
                return SessionNotFound(eventId, sessionId);

            if (!await ExportDataSource.LapFilter(db, eventId, sessionId, carNumber).AnyAsync(cancellationToken))
            {
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "No lap data",
                    detail: carNumber == null
                        ? $"Session {sessionId} of event {eventId} has no lap data to export."
                        : $"Session {sessionId} of event {eventId} has no lap data for car {carNumber}.");
            }

            context = await BuildContextAsync(db, eventId, sessionId, carNumber, session, cancellationToken);
        }

        if (!stagedExports.HasCapacity)
            return ExportsBusy(eventId, sessionId, "staged export bytes");

        var lease = await limiter.TryAcquireAsync(exportFormat, cancellationToken);
        if (lease == null)
            return ExportsBusy(eventId, sessionId, "generation slots");

        FileStream file;
        try
        {
            file = await BuildCarLapsFileAsync(context, exportFormat, cancellationToken);
        }
        finally
        {
            // The slot covers generation only. Streaming the finished file back is ordinary response
            // I/O and holding the slot for the length of a slow client's download would make the cap
            // a function of network speed rather than of the work being done. What that leaves - a
            // pile of finished files waiting on slow readers - is what the staged byte tracker bounds.
            lease.Dispose();
        }

        return File(stagedExports.Track(file), ExportFormats.ContentType(exportFormat),
            FileName(context, carNumber == null ? "laps" : $"car-{carNumber}-laps", exportFormat));
    }

    /// <summary>
    /// Downloads the pit stop and driver change report for a session as JSON, CSV or PDF.
    /// </summary>
    /// <param name="eventId">The unique identifier of the event.</param>
    /// <param name="sessionId">The unique identifier of the session.</param>
    /// <param name="format">One of <c>json</c>, <c>csv</c> or <c>pdf</c>. Defaults to <c>json</c>.</param>
    /// <returns>The generated file as an attachment.</returns>
    /// <response code="200">Returns the file. Content type is application/json, text/csv or application/pdf.</response>
    /// <response code="400">The requested format is not one of json, csv or pdf.</response>
    /// <response code="401">The event is private and the access code was missing or wrong.</response>
    /// <response code="404">The session is unknown, has not ended, or has no Flagtronics driver and pit data.</response>
    /// <response code="429">Too many export requests from this caller.</response>
    /// <response code="503">This replica is already generating its maximum number of exports; retry after the interval in the Retry-After header.</response>
    /// <remarks>
    /// The report needs both a driver identification and a pit entry time on the lap rows, and only
    /// Flagtronics supplies either, which is why it is not offered for every session. Clients are
    /// expected to check <see cref="GetAvailability"/> first; the 404 here is the defensive path for
    /// a request that arrives anyway.
    /// </remarks>
    [AllowAnonymous]
    [RequireEventAccessCode]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPitStops(int eventId, int sessionId, string? format = null)
    {
        var cancellationToken = HttpContext.RequestAborted;

        if (!ExportFormats.TryParse(format, out var exportFormat))
            return InvalidFormat(format);

        Logger.LogInformation("{m} for event {eventId}, session {sessionId}, format {format}",
            nameof(GetPitStops), eventId, sessionId, exportFormat);

        ExportContext context;
        using (var db = await tsContext.CreateDbContextAsync(cancellationToken))
        {
            var session = await LoadCompletedSessionAsync(db, eventId, sessionId, cancellationToken);
            if (session == null)
                return SessionNotFound(eventId, sessionId);

            if (!await HasFlagtronicsPitDataAsync(db, eventId, sessionId, cancellationToken))
            {
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "No pit stop data",
                    detail: $"Session {sessionId} of event {eventId} has no Flagtronics driver and pit data, " +
                            "so a pit stop and driver change report cannot be produced for it.");
            }

            context = await BuildContextAsync(db, eventId, sessionId, null, session, cancellationToken);
        }

        if (!stagedExports.HasCapacity)
            return ExportsBusy(eventId, sessionId, "staged export bytes");

        var lease = await limiter.TryAcquireAsync(exportFormat, cancellationToken);
        if (lease == null)
            return ExportsBusy(eventId, sessionId, "generation slots");

        FileStream file;
        try
        {
            file = await BuildPitStopsFileAsync(context, exportFormat, cancellationToken);
        }
        finally
        {
            lease.Dispose();
        }

        return File(stagedExports.Track(file), ExportFormats.ContentType(exportFormat),
            FileName(context, "pit-stops", exportFormat));
    }

    #region File generation

    private async Task<FileStream> BuildCarLapsFileAsync(ExportContext context, ExportFormat format,
        CancellationToken cancellationToken)
    {
        // One instance per export: the row source records what it lost into it, and the writer folds
        // that into the file so the loss is visible to whoever opens it.
        var diagnostics = new ExportScanDiagnostics();

        switch (format)
        {
            case ExportFormat.Json:
                return await ExportTempFile.BuildAsync(ExportFormats.Extension(format), asyncWrites: true,
                    async (stream, token) =>
                    {
                        var raw = ExportDataSource.StreamRawLapJsonAsync(tsContext, context.EventId, context.SessionId,
                            context.CarNumber, token);
                        var result = await LapExportWriter.WriteJsonAsync(stream, raw, context, MaxLapRows,
                            ExportBudget.MaxExportBytes, diagnostics, token);
                        LogResult(context, format, result);
                    }, cancellationToken);

            case ExportFormat.Csv:
                return await ExportTempFile.BuildAsync(ExportFormats.Extension(format), asyncWrites: true,
                    async (stream, token) =>
                    {
                        var rows = ExportDataSource.StreamLapRowsAsync(tsContext, context.EventId, context.SessionId,
                            context.CarNumber, diagnostics, Logger, token);
                        var result = await LapExportWriter.WriteCsvAsync(stream, rows, context, MaxLapRows,
                            ExportBudget.MaxExportBytes, diagnostics, token);
                        LogResult(context, format, result);
                    }, cancellationToken);

            default:
                // The PDF is the one format that has to hold its rows: page breaks are only known
                // once the layout has seen all of them. Collecting under a cap here is what keeps
                // that bounded, and the cap is deliberately an order of magnitude below the others.
                var (lapRows, truncated) = await CollectAsync(
                    ExportDataSource.StreamLapRowsAsync(tsContext, context.EventId, context.SessionId,
                        context.CarNumber, diagnostics, Logger, cancellationToken),
                    MaxPdfLapRows, cancellationToken);

                return await ExportTempFile.BuildAsync(ExportFormats.Extension(format), asyncWrites: false,
                    async (stream, token) =>
                    {
                        var result = await LapExportWriter.WritePdfAsync(stream, lapRows, context, truncated,
                            diagnostics, token);
                        LogResult(context, format, result);
                    }, cancellationToken);
        }
    }

    private async Task<FileStream> BuildPitStopsFileAsync(ExportContext context, ExportFormat format,
        CancellationToken cancellationToken)
    {
        var diagnostics = new ExportScanDiagnostics();

        switch (format)
        {
            case ExportFormat.Json:
                return await ExportTempFile.BuildAsync(ExportFormats.Extension(format), asyncWrites: true,
                    async (stream, token) =>
                    {
                        var stops = ExportDataSource.StreamPitStopsAsync(tsContext, context.EventId, context.SessionId,
                            MaxPitStopScanLaps, diagnostics, Logger, token);
                        var result = await PitStopReportWriter.WriteJsonAsync(stream, stops, context, MaxPitStopRows,
                            ExportBudget.MaxExportBytes, diagnostics, token);
                        LogResult(context, format, result);
                    }, cancellationToken);

            case ExportFormat.Csv:
                return await ExportTempFile.BuildAsync(ExportFormats.Extension(format), asyncWrites: true,
                    async (stream, token) =>
                    {
                        var stops = ExportDataSource.StreamPitStopsAsync(tsContext, context.EventId, context.SessionId,
                            MaxPitStopScanLaps, diagnostics, Logger, token);
                        var result = await PitStopReportWriter.WriteCsvAsync(stream, stops, context, MaxPitStopRows,
                            ExportBudget.MaxExportBytes, diagnostics, token);
                        LogResult(context, format, result);
                    }, cancellationToken);

            default:
                var (stopRows, truncated) = await CollectAsync(
                    ExportDataSource.StreamPitStopsAsync(tsContext, context.EventId, context.SessionId,
                        MaxPitStopScanLaps, diagnostics, Logger, cancellationToken),
                    MaxPdfPitStopRows, cancellationToken);

                return await ExportTempFile.BuildAsync(ExportFormats.Extension(format), asyncWrites: false,
                    async (stream, token) =>
                    {
                        var result = await PitStopReportWriter.WritePdfAsync(stream, stopRows, context, truncated,
                            diagnostics, token);
                        LogResult(context, format, result);
                    }, cancellationToken);
        }
    }

    /// <summary>
    /// Drains up to <paramref name="maxRows"/> items, reading one past the cap so the caller can say
    /// whether it stopped short rather than guessing from a full buffer.
    /// </summary>
    private static async Task<(List<T> Rows, bool Truncated)> CollectAsync<T>(IAsyncEnumerable<T> source, int maxRows,
        CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            if (rows.Count >= maxRows)
                return (rows, true);
            rows.Add(item);
        }
        return (rows, false);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Loads the session, or null when it is unknown or has not ended. A session that has not ended
    /// is treated the same as a missing one on purpose: from the caller's point of view there is no
    /// export of it to be had yet, and saying so as a 404 keeps the client from caching a
    /// half-finished download.
    /// </summary>
    /// <remarks>
    /// Ending is decided by <c>EndTime</c>, not by <c>IsLive</c>. The two are written together when a
    /// session finishes, but <c>IsLive</c> is also set on its own by the session monitor picking a
    /// session up - a bulk update over the event that never clears <c>EndTime</c> - so a monitor that
    /// dies leaves the flag stuck true on a session that plainly finished. Event 5 session 68 in the
    /// test environment is in exactly that state: <c>IsLive</c> true, <c>EndTime</c> set, a full
    /// session of laps behind it. Keying on the flag makes the whole feature silently unavailable for
    /// those, which is the worse failure: an end time that is stale costs a partial export, a stuck
    /// flag costs every export.
    /// </remarks>
    private static async Task<TimingCommon.Models.Session?> LoadCompletedSessionAsync(TsContext db, int eventId,
        int sessionId, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.Id == sessionId, cancellationToken);
        return session != null && HasEnded(session.EndTime, session.IsLive) ? session : null;
    }

    /// <summary>
    /// Whether a session has ended, from the two fields that each independently say so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The union, not either field alone, because each misses a different set and neither is
    /// reliable by itself.
    /// </para>
    /// <para>
    /// <c>IsLive</c> alone misses a session that finished while the flag stayed set. The flag is
    /// cleared by the session monitor, and <c>SetSessionAsLiveAsync</c> also sets it on its own - a
    /// bulk update across the event that never touches <c>EndTime</c> - so a monitor that dies leaves
    /// it stuck true. Event 5 session 68 in the test environment is exactly that: flag true, end time
    /// set, a full session of laps behind it.
    /// </para>
    /// <para>
    /// <c>EndTime</c> alone misses the opposite: a session whose row was orphaned before an end time
    /// was ever written - the processor dies mid-session, so finalization runs without knowing which
    /// session it is retiring and writes no end time, while the flag still gets cleared - and any row
    /// old enough to predate the column being populated. Those export fine and always have.
    /// </para>
    /// <para>
    /// Requiring both would drop both sets. Accepting either drops neither, and leaves exactly one
    /// ambiguous case - end time set, flag still true - which is served, but marked on the file and
    /// never cached.
    /// </para>
    /// </remarks>
    /// <param name="endTime">The session end time, if it has one.</param>
    /// <param name="isLive">The session live flag.</param>
    /// <returns>True when the session is finished as far as exporting is concerned.</returns>
    private static bool HasEnded(DateTime? endTime, bool isLive) => endTime != null || !isLive;

    /// <summary>
    /// Whether the session carries the Flagtronics data the pit report is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both a driver identification and a pit entry time are required, because only Flagtronics
    /// supplies either and a report with one but not the other has nothing to say. The Redis keys the
    /// live driver data is served from expire, so the only durable record for a finished session is
    /// what was written into the lap rows - which is where this looks.
    /// </para>
    /// <para>
    /// These are substring probes against the stored JSON rather than a parse of it, because the
    /// check has to stay cheap enough to run on every session view. A match short-circuits, but a
    /// miss does not: it would read every lap row in the session and detoast each payload to test it,
    /// which for a 24 hour race is hundreds of megabytes of I/O to answer "no". So both probes are
    /// bounded by lap number - shallow for the driver check, which is the one that answers "no" for
    /// the many sessions with no Flagtronics at all, and deeper for the pit check, which has to see
    /// past a first stint.
    /// </para>
    /// <para>
    /// The caps are why this is one method used by both the availability flag and the report's own
    /// guard rather than two checks that could disagree: whatever this concludes, the client is
    /// offered and the endpoint serves the same thing. The cost is a false negative for a session
    /// where the equipment only started reporting after every car's
    /// <see cref="DriverProbeLapDepth"/>th lap - which would mean it was installed mid-session - or
    /// where no car pitted inside its first <see cref="PitProbeLapDepth"/> laps.
    /// </para>
    /// <para>
    /// A driver source of <c>none</c> is excluded - that is the equipment reporting that it is
    /// present but has not identified anybody, which is not a driver change report.
    /// </para>
    /// </remarks>
    private static async Task<bool> HasFlagtronicsPitDataAsync(TsContext db, int eventId, int sessionId,
        CancellationToken cancellationToken)
    {
        var laps = ExportDataSource.LapFilter(db, eventId, sessionId, null);

        var hasDriverData = await laps
            .Where(l => l.LapNumber <= DriverProbeLapDepth)
            .AnyAsync(l => l.LapData.Contains(DriverSourceMarker) && !l.LapData.Contains(DriverSourceNoneMarker),
                cancellationToken);
        if (!hasDriverData)
            return false;

        return await laps
            .Where(l => l.LapNumber <= PitProbeLapDepth)
            .AnyAsync(l => l.LapData.Contains(PitEntryTimeMarker), cancellationToken);
    }

    private static async Task<ExportContext> BuildContextAsync(TsContext db, int eventId, int sessionId,
        string? carNumber, TimingCommon.Models.Session session, CancellationToken cancellationToken)
    {
        // HideName events exist so that a name is not shown before the organizer wants it shown; an
        // export must not be the one place it leaks out.
        var eventName = await db.Events.AsNoTracking()
            .Where(e => e.Id == eventId && !e.HideName)
            .Select(e => e.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return new ExportContext
        {
            EventId = eventId,
            EventName = eventName ?? string.Empty,
            SessionId = sessionId,
            SessionName = session.Name,
            CarNumber = carNumber,
            GeneratedUtc = DateTime.UtcNow,
            // Marked on the file rather than kept out of it: a session that has ended but is still
            // flagged live may have been picked up again, and nothing about the rows themselves would
            // reveal that to whoever opens the export later.
            SessionStillLive = session.EndTime != null && session.IsLive,
        };
    }

    private void LogResult(ExportContext context, ExportFormat format, ExportWriteResult result)
    {
        Logger.LogInformation("Export for event {eventId} session {sessionId} as {format}: {rows} rows, truncated {truncated}, skipped {skipped}",
            context.EventId, context.SessionId, format, result.RowsWritten, result.Truncated, result.SkippedRows);
    }

    private ObjectResult InvalidFormat(string? format)
    {
        return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported export format",
            detail: $"Format '{format}' is not supported. Use json, csv or pdf.");
    }

    private ObjectResult SessionNotFound(int eventId, int sessionId)
    {
        return Problem(statusCode: StatusCodes.Status404NotFound, title: "Session not available for export",
            detail: $"Session {sessionId} of event {eventId} does not exist or has not ended. " +
                    "Exports are only produced for sessions that have ended.");
    }

    private ObjectResult ExportsBusy(int eventId, int sessionId, string ceiling)
    {
        Logger.LogWarning(
            "Rejecting export for event {eventId} session {sessionId} on {ceiling}: {slots}/{max} generation slots and " +
            "{pdfSlots}/{pdfMax} PDF slots free after waiting {wait}; {staged} of {maxStaged} staged bytes in use",
            eventId, sessionId, ceiling, limiter.AvailableSlots, limiter.MaxConcurrentExports,
            limiter.AvailablePdfSlots, limiter.MaxConcurrentPdfRenders, limiter.WaitTimeout,
            stagedExports.StagedBytes, stagedExports.MaxStagedBytes);

        Response.Headers.RetryAfter = ExportConcurrencyLimiter.RetryAfterSeconds.ToString();
        return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Export capacity reached",
            detail: "This server is already handling the maximum number of exports. Please retry shortly.");
    }

    /// <summary>
    /// Builds the download's file name. Event and session names are not used: they are free text from
    /// an organizer and would have to be sanitized into something unrecognizable anyway, whereas the
    /// identifiers are what a person needs to find the session again.
    /// </summary>
    private static string FileName(ExportContext context, string kind, ExportFormat format)
    {
        var builder = new StringBuilder(64)
            .Append("event-").Append(context.EventId)
            .Append("-session-").Append(context.SessionId)
            .Append('-').Append(Sanitize(kind))
            .Append(ExportFormats.Extension(format));
        return builder.ToString();
    }

    /// <summary>
    /// Reduces a name fragment to characters that are safe in a file name on any platform and in a
    /// Content-Disposition header. Car numbers are free-form text from the timing system, so this is
    /// not a formality.
    /// </summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return builder.ToString();
    }

    #endregion
}
