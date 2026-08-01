using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Serialization tests for the GetCurrentSessionStateJson path: the StatusApi re-deserializes
/// the event processor's MessagePack SessionState stream, so a contract mismatch between the
/// two services surfaces here. On 2026-07-26 a status-api built against TimingCommon 1.8.0-1.12.0
/// (CarPosition key 61 = non-nullable bool) read a payload from a 1.13.0 event processor (key 61
/// removed, serialized as nil) and every call failed with "Unexpected msgpack code 192 (nil)".
/// </summary>
[TestClass]
public class EventsControllerSessionStateSerializationTests
{
    private TestEventsController _controller = null!;

    /// <summary>
    /// Lets the test supply the msgpack stream that would normally come from the
    /// event processor's /status/GetStatus endpoint.
    /// </summary>
    private class TestEventsController : RedMist.StatusApi.Controllers.V2.EventsController
    {
        public IActionResult UpstreamResult { get; set; } = new NotFoundResult();

        public TestEventsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
            HybridCache hcache, IConnectionMultiplexer cacheMux, Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache,
            IHttpClientFactory httpClientFactory)
            : base(loggerFactory, tsContext, hcache, cacheMux, memoryCache, httpClientFactory) { }

        public override Task<IActionResult> GetCurrentSessionState(int eventId) => Task.FromResult(UpstreamResult);
    }

    [TestInitialize]
    public void Setup()
    {
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _controller = new TestEventsController(
            mockLoggerFactory.Object,
            new TestDbContextFactory(options),
            new Mock<HybridCache>().Object,
            new Mock<IConnectionMultiplexer>().Object,
            new Mock<Microsoft.Extensions.Caching.Memory.IMemoryCache>().Object,
            new Mock<IHttpClientFactory>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static SessionState BuildSessionState() => new()
    {
        EventId = 297,
        SessionId = 64,
        CarPositions =
        [
            new CarPosition
            {
                EventId = "297",
                Number = "92",
                IsInPit = true,
                SignalBars = 3,
            }
        ],
    };

    private static FileStreamResult MsgPackStream(byte[] bytes) =>
        new(new MemoryStream(bytes), "application/x-msgpack");

    [TestMethod]
    public async Task GetCurrentSessionStateJson_ValidStream_ReturnsOkWithSessionState()
    {
        var bytes = MessagePackSerializer.Serialize(BuildSessionState());
        _controller.UpstreamResult = MsgPackStream(bytes);

        var result = await _controller.GetCurrentSessionStateJson(297);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        var state = ok.Value as SessionState;
        Assert.IsNotNull(state);
        Assert.AreEqual(297, state.EventId);
        Assert.AreEqual("92", state.CarPositions[0].Number);
        Assert.IsTrue(state.CarPositions[0].IsInPit);
        Assert.AreEqual(3, state.CarPositions[0].SignalBars);
    }

    [TestMethod]
    public async Task GetCurrentSessionStateJson_MalformedStream_Returns500()
    {
        // 0xC1 is the one code the msgpack spec reserves as never-used.
        _controller.UpstreamResult = MsgPackStream([0xC1, 0x00, 0x03]);

        var result = await _controller.GetCurrentSessionStateJson(297);

        var status = result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, status.StatusCode);
    }

    /// <summary>
    /// Mimics how readers built against TimingCommon 1.8.0-1.12.0 decode CarPosition: positional
    /// keys with a non-nullable bool at key 61. Removing a keyed member from CarPosition again
    /// (leaving a hole) would serialize nil in that slot and break these readers - the exact
    /// prod failure this file exists to prevent. The reader also stops at key 61, which proves
    /// newer producers can keep appending keys without breaking older consumers.
    /// </summary>
    [MessagePackObject]
    public class LegacyCarPositionReader
    {
        [Key(2)]
        public string? Number { get; set; }
        [Key(61)]
        public bool HasGps { get; set; }
    }

    [MessagePackObject]
    public class LegacySessionStateReader
    {
        [Key(0)]
        public int EventId { get; set; }
        [Key(14)]
        public List<LegacyCarPositionReader> CarPositions { get; set; } = [];
    }

    [TestMethod]
    public void CurrentSessionStatePayload_IsDecodableByLegacyBoolKey61Reader()
    {
        var bytes = MessagePackSerializer.Serialize(BuildSessionState());

        var legacy = MessagePackSerializer.Deserialize<LegacySessionStateReader>(bytes);

        Assert.AreEqual(297, legacy.EventId);
        Assert.AreEqual("92", legacy.CarPositions[0].Number);
        Assert.IsFalse(legacy.CarPositions[0].HasGps);
    }
}
