using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// Adopting a session that was already running is the restart path: unlike a new session it must
/// keep the field and the lap history that were rebuilt from the relay's cache, and it must never
/// drag the state back to the old session once a real session change has been taken.
/// </summary>
[TestClass]
public class SessionContextResumeTests
{
    private const int EventId = 9;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ResumeSession_NamesTheSessionAndKeepsTheFieldIntact()
    {
        var (context, db) = CreateContext();
        await using (db)
        {
            db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Id = EventId, Name = "Spring Classic", OrganizationId = 1 });
            await db.SaveChangesAsync(TestContext.CancellationToken);

            context.UpdateCars([new CarPosition { Number = "1" }, new CarPosition { Number = "2" }]);

            await context.ResumeSessionWithLockHeldAsync(42, "Race");

            Assert.AreEqual(42, context.SessionState.SessionId);
            Assert.AreEqual("Race", context.SessionState.SessionName);
            Assert.AreEqual(EventId, context.SessionState.EventId);
            Assert.AreEqual("Spring Classic", context.SessionState.EventName);
            Assert.HasCount(2, context.SessionState.CarPositions, "A resume must not clear the field rebuilt from the relay's cache.");
        }
    }

    /// <summary>
    /// A resume only ever names the session that was running at startup. Once a genuinely new
    /// session has been adopted, applying it would pull the state back onto the session that just
    /// ended.
    /// </summary>
    [TestMethod]
    public async Task ResumeSession_AfterADifferentSessionWasAdopted_IsIgnored()
    {
        var (context, db) = CreateContext();
        await using (db)
        {
            await context.NewSessionWithLockHeldAsync(43, "Race");

            await context.ResumeSessionWithLockHeldAsync(42, "Practice");

            Assert.AreEqual(43, context.SessionState.SessionId, "The adopted session must stand.");
            Assert.AreEqual("Race", context.SessionState.SessionName);
        }
    }

    /// <summary>
    /// Session id zero on the state means nothing has been adopted yet, not session zero - the feed
    /// does emit a valid session 0 and it has to be resumable.
    /// </summary>
    [TestMethod]
    public async Task ResumeSession_ForSessionZeroBeforeAnySessionIsAdopted_IsApplied()
    {
        var (context, db) = CreateContext();
        await using (db)
        {
            await context.ResumeSessionWithLockHeldAsync(0, "Warmup");

            Assert.AreEqual(0, context.SessionState.SessionId);
            Assert.AreEqual("Warmup", context.SessionState.SessionName);
        }
    }

    /// <summary>
    /// The guard only skips a resume naming a <em>different</em> session. The relay replays its
    /// session change on every reconnect, so a resume repeating the session already adopted is the
    /// normal case and has to be applied - it carries the session's name.
    /// </summary>
    [TestMethod]
    public async Task ResumeSession_RepeatingTheAdoptedSession_IsApplied()
    {
        var (context, db) = CreateContext();
        await using (db)
        {
            await context.NewSessionWithLockHeldAsync(42, "Race");
            context.UpdateCars([new CarPosition { Number = "1" }]);

            await context.ResumeSessionWithLockHeldAsync(42, "Race - Group B");

            Assert.AreEqual(42, context.SessionState.SessionId);
            Assert.AreEqual("Race - Group B", context.SessionState.SessionName);
            Assert.HasCount(1, context.SessionState.CarPositions, "A resume never clears the field.");
        }
    }

    /// <summary>
    /// The event name is cosmetic; a database that cannot answer for it must not stop the session
    /// being adopted at all.
    /// </summary>
    [TestMethod]
    public async Task ResumeSession_WhenTheEventNameCannotBeRead_StillAdoptsTheSession()
    {
        var failingFactory = new Mock<IDbContextFactory<TsContext>>();
        failingFactory.Setup(x => x.CreateDbContext()).Throws(new InvalidOperationException("database is down"));
        var context = new SessionContext(CreateConfiguration(), failingFactory.Object, new DebugLoggerFactory(),
            new Mock<ICarLapHistoryService>().Object);

        await context.ResumeSessionWithLockHeldAsync(42, "Race");

        Assert.AreEqual(42, context.SessionState.SessionId);
        Assert.AreEqual("Race", context.SessionState.SessionName);
        Assert.AreEqual(string.Empty, context.SessionState.EventName);
    }

    /// <summary>
    /// Class colors and ordering come from the organization behind the event. Every car's class
    /// marker is drawn from them, so the join has to reach the organization through the event.
    /// </summary>
    [TestMethod]
    public async Task SetSessionClassMetadata_PublishesTheOrganizationsClassColorsAndOrder()
    {
        var (context, db) = CreateContext();
        await using (db)
        {
            db.Organizations.Add(new Organization
            {
                Id = 5,
                Name = "Test Org",
                ShortName = "TO",
                ClientId = "client",
                Classes =
                [
                    new ClassMetadata { Name = "GTO", Order = 2, ColorHex = "#111111" },
                    new ClassMetadata { Name = "GTU", Order = 1, ColorHex = "#222222" },
                ]
            });
            db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Id = EventId, Name = "Spring Classic", OrganizationId = 5 });
            await db.SaveChangesAsync(TestContext.CancellationToken);

            context.SetSessionClassMetadata();

            Assert.AreEqual("#111111", context.SessionState.ClassColors["GTO"]);
            Assert.AreEqual("#222222", context.SessionState.ClassColors["GTU"]);
            Assert.AreEqual("2", context.SessionState.ClassOrder["GTO"]);
            Assert.AreEqual("1", context.SessionState.ClassOrder["GTU"]);
        }
    }

    /// <summary>
    /// An event whose organization has no classes configured leaves the existing metadata alone
    /// rather than blanking it.
    /// </summary>
    [TestMethod]
    public async Task SetSessionClassMetadata_WithNoMatchingOrganization_LeavesMetadataUnchanged()
    {
        var (context, db) = CreateContext();
        await using (db)
        {
            context.SessionState.ClassColors = new Dictionary<string, string> { { "GTO", "#111111" } };
            db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Id = EventId, Name = "Spring Classic", OrganizationId = 404 });
            await db.SaveChangesAsync(TestContext.CancellationToken);

            context.SetSessionClassMetadata();

            Assert.AreEqual("#111111", context.SessionState.ClassColors["GTO"]);
        }
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
        .Build();

    /// <summary>
    /// The returned context is for seeding; the session context reads through its own instances of
    /// the same in-memory store.
    /// </summary>
    private static (SessionContext Context, TsContext Db) CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var factory = new TestDbContextFactory(optionsBuilder.Options);
        var context = new SessionContext(CreateConfiguration(), factory, new DebugLoggerFactory(),
            new Mock<ICarLapHistoryService>().Object);
        return (context, factory.CreateDbContext());
    }
}
