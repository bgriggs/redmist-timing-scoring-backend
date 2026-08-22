using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Concrete stand-in for the abstract <c>EventsControllerBase</c>. The V2 controller is the real
/// production descendant; this subclass only widens access to the protected event-processor endpoint
/// lookup so the Redis/memory-cache fallback chain can be driven directly.
/// </summary>
internal class TestableEventsController : RedMist.StatusApi.Controllers.V2.EventsController
{
    public TestableEventsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        HybridCache hcache, IConnectionMultiplexer cacheMux, IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory)
        : base(loggerFactory, tsContext, hcache, cacheMux, memoryCache, httpClientFactory)
    {
    }

    public Task<string?> GetEndpointAsync(int eventId) => GetEventProcessorEndpointAsync(eventId);
}

/// <summary>
/// Returns a canned response (or throws a canned exception) for every request and records the
/// absolute URI it was asked for, so tests can assert the upstream URL the controller builds.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => this.responder = responder;

    public static StubHttpMessageHandler Returning(byte[] body) => new(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body)
    });

    public static StubHttpMessageHandler Throwing(Exception ex) => new(_ => throw ex);

    public List<string> RequestedUris { get; } = [];

    /// <summary>Set when the owning HttpClient is disposed, for tests that assert a streamed
    /// response is not torn down before the caller has read it.</summary>
    public bool Disposed { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri!.ToString());
        return Task.FromResult(responder(request));
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Wires a V2 EventsController over an isolated EF InMemory database, a real (non-mock) HybridCache
/// double, a real MemoryCache, and mocked Redis / HTTP. Each instance gets its own database name so
/// tests remain safe under MethodLevel parallelization.
/// </summary>
internal sealed class EventsControllerHarness : IDisposable
{
    public IDbContextFactory<TsContext> DbFactory { get; }
    public TsContext Db { get; }
    public FakeHybridCache Cache { get; } = new();
    public Mock<IDatabase> Redis { get; } = new();
    public Mock<IConnectionMultiplexer> Mux { get; } = new();
    public MemoryCache MemoryCache { get; } = new(new MemoryCacheOptions());
    public Mock<IHttpClientFactory> HttpClientFactory { get; } = new();
    public Mock<ILogger> Logger { get; } = new();
    public ILoggerFactory LoggerFactory { get; }
    public TestableEventsController Controller { get; }

    public EventsControllerHarness()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        DbFactory = new TestDbContextFactory(options);
        Db = DbFactory.CreateDbContext();

        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(Logger.Object);
        LoggerFactory = loggerFactory.Object;
        Mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(() => Redis.Object);

        Controller = new TestableEventsController(loggerFactory.Object, DbFactory, Cache, Mux.Object,
            MemoryCache, HttpClientFactory.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>Builds the V1 controller over the same database and caches as <see cref="Controller"/>.</summary>
    public RedMist.StatusApi.Controllers.V1.EventsController CreateV1Controller() =>
        new(LoggerFactory, DbFactory, Cache, Mux.Object, MemoryCache, HttpClientFactory.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    /// <summary>Makes a single Redis string key resolve to <paramref name="value"/>; all other keys stay empty.</summary>
    public void SetRedisString(string key, string value) =>
        Redis.Setup(x => x.StringGetAsync((RedisKey)key, It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(value));

    public void SetRedisThrows(Exception ex) =>
        Redis.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ThrowsAsync(ex);

    /// <summary>Installs the handler behind the named "EventProcessor" client, returning a fresh HttpClient per call.</summary>
    /// <param name="disposeHandler">
    /// When true the returned client owns the handler, so disposing the client marks
    /// <see cref="StubHttpMessageHandler.Disposed"/>. That makes an early disposal by the controller
    /// observable, which is otherwise invisible to a stubbed transport.
    /// </param>
    public StubHttpMessageHandler SetUpstream(StubHttpMessageHandler handler, bool disposeHandler = false)
    {
        HttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler));
        return handler;
    }

    public Organization AddOrganization(int id, string name = "Test Org", string? website = null,
        string controlLogType = "", string clientId = "relay-1", byte[]? logo = null)
    {
        var org = new Organization
        {
            Id = id,
            Name = name,
            ShortName = "TST",
            Website = website,
            ControlLogType = controlLogType,
            ClientId = clientId,
            Logo = logo,
        };
        Db.Organizations.Add(org);
        return org;
    }

    public ConfigEvent AddEvent(string name = "Event", int organizationId = 1, DateTime? startDate = null,
        bool isLive = false, bool isActive = false, bool isDeleted = false, bool isArchived = false,
        bool isPrivate = false, bool hideName = false, bool isSimulation = false, string trackName = "Track")
    {
        var evt = new ConfigEvent
        {
            Name = name,
            OrganizationId = organizationId,
            StartDate = startDate ?? new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc),
            IsLive = isLive,
            IsActive = isActive,
            IsDeleted = isDeleted,
            IsArchived = isArchived,
            IsPrivate = isPrivate,
            HideName = hideName,
            IsSimulation = isSimulation,
            TrackName = trackName,
        };
        Db.Events.Add(evt);
        return evt;
    }

    /// <summary>
    /// Adds a session that produced results, which is what the session lists are for. Pass
    /// <paramref name="withResults"/> false for a session the timing system announced but that never
    /// saw a car - the lists leave those out, so a test wanting one in a list has to give it laps.
    /// </summary>
    public void AddSession(int id, int eventId, string name = "Race", bool withResults = true)
    {
        Db.Sessions.Add(new Session { Id = id, EventId = eventId, Name = name });
        if (withResults)
        {
            Db.SessionResults.Add(new RedMist.Database.Models.SessionResult { EventId = eventId, SessionId = id });
        }
    }

    public void AddLap(int eventId, int sessionId, string carNumber, int lapNumber, string? lapData = null)
    {
        Db.CarLapLogs.Add(new RedMist.Database.Models.CarLapLog
        {
            EventId = eventId,
            SessionId = sessionId,
            CarNumber = carNumber,
            LapNumber = lapNumber,
            Timestamp = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc),
            LapData = lapData ?? System.Text.Json.JsonSerializer.Serialize(
                new CarPosition { Number = carNumber, LastLapCompleted = lapNumber, EventId = eventId.ToString() }),
        });
    }

    public void Dispose()
    {
        Db.Dispose();
        MemoryCache.Dispose();
    }
}
