using MessagePack;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RedMist.Backend.Shared;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Covers the live-data paths of EventsControllerBase: resolving an event's processor pod from Redis
/// (and the memory-cache short circuit in front of it), proxying the live session state, and the
/// Redis-backed control-log / in-car reads whose cache-miss behavior differs per endpoint.
/// </summary>
[TestClass]
public class EventsControllerBaseProcessorAndCacheTests
{
    private EventsControllerHarness _h = null!;

    [TestInitialize]
    public void Setup() => _h = new EventsControllerHarness();

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    private static string EndpointKey(int eventId) => string.Format(Consts.EVENT_SERVICE_ENDPOINT, eventId);

    #region GetEventProcessorEndpointAsync

    /// <summary>
    /// The pods register a bare host:port. Without the scheme fix-up, HttpClient would reject the URL
    /// and every live-status poll for the event would fail.
    /// </summary>
    [TestMethod]
    public async Task GetEventProcessorEndpoint_ValueWithoutScheme_IsPrefixedWithHttp()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");

        var url = await _h.Controller.GetEndpointAsync(7);

        Assert.AreEqual("http://evt-7-processor.timing:8080", url);
    }

    [TestMethod]
    public async Task GetEventProcessorEndpoint_ValueWithScheme_IsLeftUnchanged()
    {
        _h.SetRedisString(EndpointKey(7), "https://evt-7-processor.timing");

        var url = await _h.Controller.GetEndpointAsync(7);

        Assert.AreEqual("https://evt-7-processor.timing", url);
    }

    [TestMethod]
    public async Task GetEventProcessorEndpoint_SecondCall_ServedFromMemoryCacheWithoutHittingRedis()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");

        var first = await _h.Controller.GetEndpointAsync(7);
        var second = await _h.Controller.GetEndpointAsync(7);

        Assert.AreEqual(first, second);
        _h.Redis.Verify(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// A missing key must not be cached: the pod may not have registered yet, and a negative cache
    /// entry would keep the event dark for a full day.
    /// </summary>
    [TestMethod]
    public async Task GetEventProcessorEndpoint_MissingFromRedis_ReturnsNullAndIsNotCached()
    {
        var first = await _h.Controller.GetEndpointAsync(7);
        var second = await _h.Controller.GetEndpointAsync(7);

        Assert.IsNull(first);
        Assert.IsNull(second);
        _h.Redis.Verify(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Exactly(2));
    }

    /// <summary>A Redis outage degrades to "no endpoint" rather than a 500 out of the controller.</summary>
    [TestMethod]
    public async Task GetEventProcessorEndpoint_RedisThrows_ReturnsNull()
    {
        _h.SetRedisThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var url = await _h.Controller.GetEndpointAsync(7);

        Assert.IsNull(url);
    }

    [TestMethod]
    public async Task GetEventProcessorEndpoint_UsesAPerEventKey()
    {
        _h.SetRedisString(EndpointKey(7), "seven:8080");
        _h.SetRedisString(EndpointKey(8), "eight:8080");

        Assert.AreEqual("http://seven:8080", await _h.Controller.GetEndpointAsync(7));
        Assert.AreEqual("http://eight:8080", await _h.Controller.GetEndpointAsync(8));
    }

    #endregion

    #region GetCurrentSessionState

    private static byte[] SessionStateBytes(int eventId = 7, int sessionId = 3) =>
        MessagePackSerializer.Serialize(new SessionState { EventId = eventId, SessionId = sessionId });

    [TestMethod]
    public async Task GetCurrentSessionState_NoRegisteredEndpoint_ReturnsNotFound()
    {
        var result = await _h.Controller.GetCurrentSessionState(7);

        var notFound = result as NotFoundObjectResult;
        Assert.IsNotNull(notFound);
        Assert.AreEqual("Event processor endpoint not found", notFound.Value);
    }

    /// <summary>
    /// The stored endpoint may carry a trailing slash; the status path must still be appended exactly
    /// once so the processor's route matches.
    /// </summary>
    [TestMethod]
    public async Task GetCurrentSessionState_BuildsStatusUrlFromEndpointAndTrimsTrailingSlash()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080/");
        var handler = _h.SetUpstream(StubHttpMessageHandler.Returning(SessionStateBytes()));

        var result = await _h.Controller.GetCurrentSessionState(7);

        Assert.IsInstanceOfType<FileStreamResult>(result);
        Assert.AreEqual("http://evt-7-processor.timing:8080/status/GetStatus", handler.RequestedUris.Single());
    }

    [TestMethod]
    public async Task GetCurrentSessionState_UpstreamOk_ReturnsMsgPackStream()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");
        _h.SetUpstream(StubHttpMessageHandler.Returning(SessionStateBytes(7, 3)));

        var result = await _h.Controller.GetCurrentSessionState(7);

        var file = result as FileStreamResult;
        Assert.IsNotNull(file);
        Assert.AreEqual("application/x-msgpack", file.ContentType);
        var state = await MessagePackSerializer.DeserializeAsync<SessionState>(file.FileStream);
        Assert.AreEqual(7, state.EventId);
        Assert.AreEqual(3, state.SessionId);
    }

    /// <summary>
    /// A timed-out poll must be distinguishable from a missing event: the apps back off on 408 but
    /// treat 404 as "this event has no live data".
    /// </summary>
    [TestMethod]
    public async Task GetCurrentSessionState_UpstreamTimeout_Returns408()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");
        _h.SetUpstream(StubHttpMessageHandler.Throwing(new TaskCanceledException("timed out", new TimeoutException())));

        var result = await _h.Controller.GetCurrentSessionState(7);

        var status = result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status408RequestTimeout, status.StatusCode);
    }

    [TestMethod]
    public async Task GetCurrentSessionState_UpstreamConnectionFailure_ReturnsNotFound()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");
        _h.SetUpstream(StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused")));

        var result = await _h.Controller.GetCurrentSessionState(7);

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    #endregion

    #region GetCurrentSessionStateJson / GetCurrentLegacySessionPayload

    [TestMethod]
    public async Task GetCurrentSessionStateJson_NoEndpoint_PropagatesUpstreamNotFound()
    {
        var result = await _h.Controller.GetCurrentSessionStateJson(7);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// The legacy endpoint converts the processor's SessionState into the retired Payload shape; the
    /// EventStatus block it synthesizes is what old clients read the flag and clock from.
    /// </summary>
    [TestMethod]
#pragma warning disable CS0618
    public async Task GetCurrentLegacySessionPayload_ValidStream_ConvertsToPayload()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");
        var state = new SessionState
        {
            EventId = 7,
            EventName = "Spring Enduro",
            CurrentFlag = Flags.Yellow,
            RunningRaceTime = "01:23:45",
            CarPositions = [new CarPosition { Number = "42", OverallPosition = 1 }],
        };
        _h.SetUpstream(StubHttpMessageHandler.Returning(MessagePackSerializer.Serialize(state)));

        var result = await _h.Controller.GetCurrentLegacySessionPayload(7);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        var payload = ok.Value as Payload;
        Assert.IsNotNull(payload);
        Assert.AreEqual(7, payload.EventId);
        Assert.AreEqual("Spring Enduro", payload.EventName);
        Assert.AreEqual("7", payload.EventStatus!.EventId);
        Assert.AreEqual(Flags.Yellow, payload.EventStatus.Flag);
        Assert.AreEqual("01:23:45", payload.EventStatus.RunningRaceTime);
        Assert.AreEqual("42", payload.CarPositions!.Single().Number);
    }
#pragma warning restore CS0618

    [TestMethod]
    public async Task GetCurrentLegacySessionPayload_MalformedStream_Returns500()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");
        // 0xC1 is the one byte the msgpack spec reserves as never-valid.
        _h.SetUpstream(StubHttpMessageHandler.Returning([0xC1, 0x00, 0x03]));

        var result = await _h.Controller.GetCurrentLegacySessionPayload(7);

        var status = result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, status.StatusCode);
    }

    [TestMethod]
    public async Task GetCurrentLegacySessionPayload_NoEndpoint_PropagatesUpstreamNotFound()
    {
        var result = await _h.Controller.GetCurrentLegacySessionPayload(7);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// A processor that has not adopted a session yet answers with a msgpack nil, which deserializes
    /// to null. That is "no live data", not a malformed payload, so it must be a 404 and not a 500.
    /// </summary>
    [TestMethod]
    public async Task GetCurrentSessionStateJson_UpstreamReturnsMsgPackNil_ReturnsNotFound()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");
        _h.SetUpstream(StubHttpMessageHandler.Returning([0xC0]));

        var result = await _h.Controller.GetCurrentSessionStateJson(7);

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    [TestMethod]
    public async Task GetCurrentLegacySessionPayload_UpstreamReturnsMsgPackNil_ReturnsNotFound()
    {
        _h.SetRedisString(EndpointKey(7), "evt-7-processor.timing:8080");
        _h.SetUpstream(StubHttpMessageHandler.Returning([0xC0]));

        var result = await _h.Controller.GetCurrentLegacySessionPayload(7);

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    #endregion

    #region Control logs

    /// <summary>
    /// The live control log lives only in Redis. A cache miss means the organizer has not configured
    /// one (or the puller is behind), which the apps render as "no entries" - not as an error.
    /// </summary>
    [TestMethod]
    public async Task LoadControlLog_NotInCache_ReturnsEmptyList()
    {
        var entries = await _h.Controller.LoadControlLog(7);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task LoadControlLog_ReturnsEntriesFromCache()
    {
        var logs = new CarControlLogs
        {
            CarNumber = "42",
            ControlLogEntries =
            [
                new ControlLogEntry { OrderId = 1, Car1 = "42", Note = "Contact turn 4", PenaltyAction = "Warning" },
                new ControlLogEntry { OrderId = 2, Car1 = "42", Note = "Black flag", PenaltyAction = "Stop and go" },
            ]
        };
        _h.SetRedisString(string.Format(Consts.CONTROL_LOG, 7), JsonSerializer.Serialize(logs));

        var entries = await _h.Controller.LoadControlLog(7);

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("Contact turn 4", entries[0].Note);
        Assert.AreEqual("Stop and go", entries[1].PenaltyAction);
    }

    /// <summary>A literal JSON null in the cache deserializes to null and must fall back to an empty list.</summary>
    [TestMethod]
    public async Task LoadControlLog_NullJsonInCache_ReturnsEmptyList()
    {
        _h.SetRedisString(string.Format(Consts.CONTROL_LOG, 7), "null");

        var entries = await _h.Controller.LoadControlLog(7);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task LoadSessionHistoricalControlLog_NoSessionResultRow_ReturnsEmptyList()
    {
        var entries = await _h.Controller.LoadSessionHistoricalControlLog(7, 3);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task LoadSessionHistoricalControlLog_ReturnsLogsForThatSessionOnly()
    {
        _h.Db.SessionResults.AddRange(
            new SessionResult
            {
                EventId = 7,
                SessionId = 3,
                ControlLogs = [new ControlLogEntry { OrderId = 1, Note = "Session 3 note" }]
            },
            new SessionResult
            {
                EventId = 7,
                SessionId = 4,
                ControlLogs = [new ControlLogEntry { OrderId = 1, Note = "Session 4 note" }]
            });
        await _h.Db.SaveChangesAsync();

        var entries = await _h.Controller.LoadSessionHistoricalControlLog(7, 3);

        Assert.AreEqual("Session 3 note", entries.Single().Note);
    }

    /// <summary>
    /// Per-car control logs return null rather than an empty object on a miss, so the app can tell
    /// "no data published" apart from "published, but this car is clean".
    /// </summary>
    [TestMethod]
    public async Task LoadCarControlLogs_NotInCache_ReturnsNull()
    {
        var logs = await _h.Controller.LoadCarControlLogs(7, "42");

        Assert.IsNull(logs);
    }

    [TestMethod]
    public async Task LoadCarControlLogs_ReturnsDeserializedLogsForRequestedCar()
    {
        var stored = new CarControlLogs
        {
            CarNumber = "42",
            ControlLogEntries = [new ControlLogEntry { OrderId = 9, Car1 = "42", Status = "Under review" }]
        };
        _h.SetRedisString(string.Format(Consts.CONTROL_LOG_CAR, 7, "42"), JsonSerializer.Serialize(stored));

        var logs = await _h.Controller.LoadCarControlLogs(7, "42");

        Assert.IsNotNull(logs);
        Assert.AreEqual("42", logs.CarNumber);
        Assert.AreEqual("Under review", logs.ControlLogEntries.Single().Status);
        Assert.IsNull(await _h.Controller.LoadCarControlLogs(7, "7"), "a different car must not read car 42's key");
    }

    #endregion

    #region In-car payload

    [TestMethod]
    public async Task LoadInCarPayload_NotInCache_ReturnsNull()
    {
        var payload = await _h.Controller.LoadInCarPayload(7, "42");

        Assert.IsNull(payload);
    }

    [TestMethod]
    public async Task LoadInCarPayload_ReturnsDeserializedPayloadForRequestedCar()
    {
        var stored = new InCarPayload
        {
            CarNumber = "42",
            PositionOverall = "3",
            PositionInClass = "1",
            Flag = Flags.Green,
        };
        _h.SetRedisString(string.Format(Consts.IN_CAR_DATA, 7, "42"), JsonSerializer.Serialize(stored));

        var payload = await _h.Controller.LoadInCarPayload(7, "42");

        Assert.IsNotNull(payload);
        Assert.AreEqual("42", payload.CarNumber);
        Assert.AreEqual("3", payload.PositionOverall);
        Assert.AreEqual(Flags.Green, payload.Flag);
        Assert.IsNull(await _h.Controller.LoadInCarPayload(7, "7"), "a different car must not read car 42's key");
    }

    #endregion
}
