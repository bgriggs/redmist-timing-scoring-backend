using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.StatusApi.Controllers.V1;
using RedMist.StatusApi.Services.Exports;
using RedMist.TimingCommon.Models;
using System.Text.Json;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Wires an <see cref="ExportsController"/> over an isolated EF InMemory database. Each instance
/// gets its own database name so the tests stay safe under MethodLevel parallelization.
/// </summary>
internal sealed class ExportsControllerHarness : IDisposable
{
    public IDbContextFactory<TsContext> DbFactory { get; }
    public TsContext Db { get; }
    public ExportConcurrencyLimiter Limiter { get; }
    public StagedExportTracker StagedExports { get; }
    public FakeHybridCache Cache { get; } = new();
    public Mock<ILogger> Logger { get; } = new();
    public ExportsController Controller { get; }

    /// <summary>
    /// Builds the harness.
    /// </summary>
    /// <param name="maxConcurrentExports">Export slots. Pass 1 and hold a lease to force the busy path.</param>
    /// <param name="waitTimeout">
    /// How long the controller waits for a slot. Defaults to almost nothing so the saturation test
    /// does not spend the production three second timeout proving the point.
    /// </param>
    /// <param name="maxConcurrentPdfRenders">PDF render slots, taken on top of a general slot.</param>
    /// <param name="maxStagedBytes">Ceiling on undelivered export bytes; lowered to force the refusal path.</param>
    /// <param name="dbFactory">
    /// Overrides the context factory, so a test can make generation fail after validation has passed.
    /// </param>
    public ExportsControllerHarness(int maxConcurrentExports = 2, TimeSpan? waitTimeout = null,
        int maxConcurrentPdfRenders = 1, long maxStagedBytes = StagedExportTracker.DefaultMaxStagedBytes,
        Func<IDbContextFactory<TsContext>, IDbContextFactory<TsContext>>? dbFactory = null)
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        DbFactory = new TestDbContextFactory(options);
        Db = DbFactory.CreateDbContext();

        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(Logger.Object);

        Limiter = new ExportConcurrencyLimiter(maxConcurrentExports, waitTimeout ?? TimeSpan.FromMilliseconds(20),
            maxConcurrentPdfRenders);
        StagedExports = new StagedExportTracker(maxStagedBytes);

        var controllerFactory = dbFactory?.Invoke(DbFactory) ?? DbFactory;
        Controller = new ExportsController(loggerFactory.Object, controllerFactory, Limiter, StagedExports, Cache)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            // ControllerBase.Problem() resolves this from request services, which a DefaultHttpContext
            // does not have, so it is supplied directly rather than standing up a container.
            ProblemDetailsFactory = new StubProblemDetailsFactory(),
        };
    }

    public ConfigEvent AddEvent(int id, string name = "Test Event", bool hideName = false)
    {
        var evt = new ConfigEvent
        {
            Id = id,
            Name = name,
            OrganizationId = 1,
            StartDate = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc),
            HideName = hideName,
        };
        Db.Events.Add(evt);
        return evt;
    }

    /// <summary>
    /// Adds a session. Whether it counts as finished is <paramref name="ended"/> - the end time -
    /// not <paramref name="isLive"/>; the two are independent in real data, which is the point of
    /// being able to set them separately here.
    /// </summary>
    /// <param name="eventId">Owning event.</param>
    /// <param name="sessionId">Session id.</param>
    /// <param name="name">Session name.</param>
    /// <param name="isLive">The live flag, which the session monitor sets and may leave stale.</param>
    /// <param name="ended">Whether the session has an end time, which is what decides exportability.</param>
    /// <param name="timeZoneOffset">
    /// Hours from UTC at the track. Defaults to -4, which is what event 382 session 88 reported, so
    /// the tests exercise the conversion rather than the identity case.
    /// </param>
    public Session AddSession(int eventId, int sessionId, string name = "Race", bool isLive = false,
        bool ended = true, double timeZoneOffset = -4)
    {
        var session = new Session
        {
            Id = sessionId,
            EventId = eventId,
            Name = name,
            IsLive = isLive,
            LocalTimeZoneOffset = timeZoneOffset,
            EndTime = ended ? new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc) : null,
        };
        Db.Sessions.Add(session);
        return session;
    }

    /// <summary>
    /// Adds one lap row, serialized exactly the way the event processor writes it, so the substring
    /// probes the availability check uses are exercised against real output rather than a fixture.
    /// </summary>
    public void AddLap(int eventId, int sessionId, CarPosition position, DateTime? timestamp = null,
        Flags flag = Flags.Green, string? rawLapData = null)
    {
        Db.CarLapLogs.Add(new RedMist.Database.Models.CarLapLog
        {
            EventId = eventId,
            SessionId = sessionId,
            CarNumber = position.Number ?? string.Empty,
            LapNumber = position.LastLapCompleted,
            Timestamp = timestamp ?? new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc)
                .AddMinutes(position.LastLapCompleted),
            Flag = (int)flag,
            LapData = rawLapData ?? JsonSerializer.Serialize(position),
        });
    }

    /// <summary>Builds a lap snapshot with just the fields the exports care about.</summary>
    public static CarPosition Lap(string car, int lap, string? driver = null, string? driverSource = null,
        DateTime? pitEntry = null, int? pitDurationMs = null, bool lapIncludedPit = false,
        string? lapTime = null, string? carClass = null, bool isInPit = false)
    {
        return new CarPosition
        {
            IsInPit = isInPit,
            Number = car,
            LastLapCompleted = lap,
            DriverName = driver ?? string.Empty,
            DriverSource = driverSource,
            PitEntryTime = pitEntry,
            PitDurationMs = pitDurationMs,
            LapIncludedPit = lapIncludedPit,
            LastLapTime = lapTime,
            Class = carClass,
            OverallPosition = 1,
            ClassPosition = 1,
        };
    }

    public Task SaveAsync() => Db.SaveChangesAsync();

    /// <summary>
    /// Reaches into a returned file result for the staged path, through the tracking wrapper the
    /// controller puts around it.
    /// </summary>
    public static string StagedPath(Stream stream) =>
        (string)stream.GetType().GetProperty("Path")!.GetValue(stream)!;

    public void Dispose()
    {
        Db.Dispose();
        Limiter.Dispose();
    }
}

/// <summary>
/// Minimal <see cref="ProblemDetailsFactory"/> so <c>ControllerBase.Problem()</c> works without a
/// service provider. It only has to preserve the status code and detail the tests assert on.
/// </summary>
internal sealed class StubProblemDetailsFactory : ProblemDetailsFactory
{
    public override ProblemDetails CreateProblemDetails(HttpContext httpContext, int? statusCode = null,
        string? title = null, string? type = null, string? detail = null, string? instance = null)
    {
        return new ProblemDetails
        {
            Status = statusCode ?? StatusCodes.Status500InternalServerError,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = instance,
        };
    }

    public override ValidationProblemDetails CreateValidationProblemDetails(HttpContext httpContext,
        ModelStateDictionary modelStateDictionary, int? statusCode = null, string? title = null,
        string? type = null, string? detail = null, string? instance = null)
    {
        return new ValidationProblemDetails(modelStateDictionary)
        {
            Status = statusCode ?? StatusCodes.Status400BadRequest,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = instance,
        };
    }
}
