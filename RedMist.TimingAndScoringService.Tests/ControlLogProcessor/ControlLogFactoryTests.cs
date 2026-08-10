using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.ControlLogs;
using RedMist.ControlLogs.Announcements;
using RedMist.Database;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.ControlLogProcessor;

/// <summary>
/// The factory is the only place the organization's stored <c>ControlLogType</c> string is turned into a
/// parser, so a mis-wired branch silently parses one series' sheet with another series' column layout.
/// </summary>
[TestClass]
public class ControlLogFactoryTests
{
    private static ControlLogFactory Factory(IAnnouncementControlLogStore? store = null) =>
        new(NullLoggerFactory.Instance, new ConfigurationBuilder().Build(), Mock.Of<IDbContextFactory<TsContext>>(), store);

    [TestMethod]
    public void CreateControlLog_EveryTypeInTheAdvertisedListIsSupported()
    {
        foreach (var type in ControlLogType.Types)
        {
            using var log = Factory().CreateControlLog(type);
            Assert.AreEqual(type, log.Type);
        }
    }

    [TestMethod]
    public void CreateControlLog_UnknownType_Throws()
    {
        Assert.ThrowsExactly<NotImplementedException>(() => Factory().CreateControlLog("GoogleDocsControlLog"));
    }

    [TestMethod]
    public async Task CreateControlLog_Announcement_ReadsFromTheSharedStore()
    {
        var store = new AnnouncementControlLogStore();
        store.Set([new ControlLogEntry { OrderId = 1, Car1 = "14", Note = "CAR 14 BEHIND THE WALL" }]);
        using var log = Factory(store).CreateControlLog(ControlLogType.ANNOUNCEMENT);

        var (success, entries) = await log.LoadControlLogAsync("unused");

        Assert.IsTrue(success);
        Assert.AreEqual("14", entries.Single().Car1);
    }

    [TestMethod]
    public async Task CreateControlLog_Announcement_WithoutAStore_YieldsAnEmptyLogRatherThanThrowing()
    {
        // Hosts other than the control log processor resolve the factory with no store registered.
        using var log = Factory().CreateControlLog(ControlLogType.ANNOUNCEMENT);

        var (success, entries) = await log.LoadControlLogAsync("unused");

        Assert.IsTrue(success);
        Assert.AreEqual(0, entries.Count());
    }
}
