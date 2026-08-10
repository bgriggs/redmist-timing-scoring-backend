using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Services;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using Event = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The gate in front of private events. Everything here is about failing closed and about not
/// turning every subscribe into a database round trip.
/// </summary>
[TestClass]
public class EventAccessValidatorTests
{
    private const int EventId = 77;

    private IDbContextFactory<TsContext> dbFactory = null!;
    private FakeHybridCache hcache = null!;

    [TestInitialize]
    public void Setup()
    {
        hcache = new FakeHybridCache();
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"EventAccessValidatorTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);
    }

    private EventAccessValidator CreateValidator()
        => new(dbFactory, hcache, new DebugLoggerFactory().CreateLogger<EventAccessValidator>());

    private async Task SeedEventAsync(bool isPrivate, string? accessCode)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Events.Add(new Event
        {
            Id = EventId,
            OrganizationId = 1,
            Name = "Test Event",
            IsPrivate = isPrivate,
            AccessCode = accessCode,
        });
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ValidateAsync_ForAPublicEvent_AllowsClientsWithNoCode()
    {
        await SeedEventAsync(isPrivate: false, accessCode: null);
        var validator = CreateValidator();

        Assert.IsTrue(await validator.ValidateAsync(EventId, null));
        Assert.IsTrue(await validator.ValidateAsync(EventId, "anything"));
    }

    /// <summary>
    /// An event id that is not in the database is treated as public rather than as an error; the
    /// caller's own lookup will fail on it soon enough.
    /// </summary>
    [TestMethod]
    public async Task ValidateAsync_ForAnUnknownEvent_Allows()
    {
        var validator = CreateValidator();

        Assert.IsTrue(await validator.ValidateAsync(EventId, null));
        Assert.AreEqual(new EventAccess(false, null), await validator.GetAccessAsync(EventId));
    }

    [TestMethod]
    public async Task ValidateAsync_ForAPrivateEvent_AcceptsTheExactCode()
    {
        await SeedEventAsync(isPrivate: true, accessCode: "1234567");
        var validator = CreateValidator();

        Assert.IsTrue(await validator.ValidateAsync(EventId, "1234567"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("7654321")]
    [DataRow("ABCDEFG")]
    [DataRow("Abcdefg")]
    [DataRow(" abcdefg")]
    [DataRow("abcdefg ")]
    public async Task ValidateAsync_ForAPrivateEvent_RejectsAnythingButAnExactMatch(string? supplied)
    {
        await SeedEventAsync(isPrivate: true, accessCode: "abcdefg");
        var validator = CreateValidator();

        Assert.IsFalse(await validator.ValidateAsync(EventId, supplied),
            "the comparison is ordinal, so case and whitespace differences are rejections");
    }

    /// <summary>
    /// A private event with no code configured is a misconfiguration. Treating it as public would
    /// publish the event; access is denied instead.
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task ValidateAsync_ForAPrivateEventWithNoCodeConfigured_DeniesEveryone(string? configuredCode)
    {
        await SeedEventAsync(isPrivate: true, accessCode: configuredCode);
        var validator = CreateValidator();

        Assert.IsFalse(await validator.ValidateAsync(EventId, null));
        Assert.IsFalse(await validator.ValidateAsync(EventId, ""));
        Assert.IsFalse(await validator.ValidateAsync(EventId, "guess"));
    }

    [TestMethod]
    public async Task GetAccessAsync_ReadsTheDatabaseOncePerEvent()
    {
        await SeedEventAsync(isPrivate: true, accessCode: "1234567");
        var validator = CreateValidator();

        await validator.ValidateAsync(EventId, "1234567");
        await validator.ValidateAsync(EventId, "1234567");
        await validator.GetAccessAsync(EventId);

        CollectionAssert.AreEqual(new[] { string.Format(Consts.EVENT_ACCESS, EventId) }, hcache.FactoryInvocations,
            "this runs on every subscribe and every API call, so it has to be served from cache");
    }

    /// <summary>
    /// Without invalidation an organizer turning privacy on or off would not take effect until the
    /// cache entry expired.
    /// </summary>
    [TestMethod]
    public async Task InvalidateAsync_MakesTheNextLookupSeeTheNewConfiguration()
    {
        await SeedEventAsync(isPrivate: false, accessCode: null);
        var validator = CreateValidator();
        Assert.IsTrue(await validator.ValidateAsync(EventId, null));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var ev = await db.Events.SingleAsync();
            ev.IsPrivate = true;
            ev.AccessCode = "1234567";
            await db.SaveChangesAsync();
        }

        Assert.IsTrue(await validator.ValidateAsync(EventId, null), "the stale cached copy is still in play");

        await validator.InvalidateAsync(EventId);

        Assert.IsFalse(await validator.ValidateAsync(EventId, null));
        Assert.IsTrue(await validator.ValidateAsync(EventId, "1234567"));
    }
}
