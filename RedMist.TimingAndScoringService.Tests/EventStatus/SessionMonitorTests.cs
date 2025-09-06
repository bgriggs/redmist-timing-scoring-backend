using BigMission.TestHelpers.Testing;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.FlagData;
using RedMist.TimingAndScoringService.EventStatus.InCarDriverMode;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.EventStatus;

[TestClass]
public class SessionMonitorTests
{
    private readonly DebugLoggerFactory lf = new();


    [TestMethod]
    public async Task Session_End_CarsFinishing_Test()
    {
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var session = new DebugSessionMonitor(1, dbMock.Object);
        var mediatorMock = new Mock<IMediator>();
        var pitProcessor = new PitProcessor(1, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(1, dbMock.Object, lf);
        var cacheMux = new Mock<IConnectionMultiplexer>();
        var db = new Mock<IDbContextFactory<TsContext>>();
        var hub = new Mock<IHubContext<StatusHub>>();
        var hcache = new Mock<HybridCache>();
        var dmProc = new DriverModeProcessor(1, hub.Object, lf, hcache.Object, db.Object, cacheMux.Object);
        var processor = new RMonitorDataProcessor(1, mediatorMock.Object, lf, session, pitProcessor, flagProcessor, cacheMux.Object, db.Object, dmProc);

        var dataReader = new TestDataReader("event-finish-with-cars-data.log");
        var data = dataReader.GetData();

        int finalCount = 0;
        session.FinalizedSession += () =>
        {
            finalCount++;
        };

        int count = 0;
        foreach (var cmd in data)
        {
            count++;
            //if (cmd.Contains("Finish"))
            //{
            //    Console.WriteLine("Finish command");
            //}

            await processor.ProcessUpdate("rmonitor", cmd, 36);

            if (count % 10 == 0)
            {
                await processor.GetPayload();
            }
        }

        Assert.IsTrue(finalCount > 1);
    }

    [TestMethod]
    public async Task Session_End_EventStopping_Test()
    {
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var session = new DebugSessionMonitor(1, dbMock.Object);
        var mediatorMock = new Mock<IMediator>();
        var pitProcessor = new PitProcessor(1, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(1, dbMock.Object, lf);
        var cacheMux = new Mock<IConnectionMultiplexer>();
        var db = new Mock<IDbContextFactory<TsContext>>();
        var hub = new Mock<IHubContext<StatusHub>>();
        var hcache = new Mock<HybridCache>();
        var dmProc = new DriverModeProcessor(1, hub.Object, lf, hcache.Object, db.Object, cacheMux.Object);
        var processor = new RMonitorDataProcessor(1, mediatorMock.Object, lf, session, pitProcessor, flagProcessor, cacheMux.Object, db.Object, dmProc);

        var dataReader = new TestDataReader("event-finish-with-stopped.log");
        var data = dataReader.GetData();

        int finalCount = 0;
        session.FinalizedSession += () =>
        {
            finalCount++;
        };

        int count = 0;
        foreach (var cmd in data)
        {
            count++;
            //if (cmd.Contains("Finish"))
            {
                //Console.WriteLine("Finish command");
            }

            await processor.ProcessUpdate("rmonitor", cmd, 36);

            if (count % 10 == 0)
            {
                await processor.GetPayload();
            }
        }

        // Call get payload as if the 5 second timer would do for full status updates
        // First call to flush any remaining position updates in the mod 10 gap
        await processor.GetPayload();
        await processor.GetPayload();

        Assert.IsTrue(finalCount > 1);
    }

    [TestMethod]
    public async Task Session_End_Reset_Test()
    {
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var session = new DebugSessionMonitor(1, dbMock.Object);
        var mediatorMock = new Mock<IMediator>();
        var pitProcessor = new PitProcessor(1, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(1, dbMock.Object, lf);
        var cacheMux = new Mock<IConnectionMultiplexer>();
        var db = new Mock<IDbContextFactory<TsContext>>();
        var hub = new Mock<IHubContext<StatusHub>>();
        var hcache = new Mock<HybridCache>();
        var dmProc = new DriverModeProcessor(1, hub.Object, lf, hcache.Object, db.Object, cacheMux.Object);
        var processor = new RMonitorDataProcessor(1, mediatorMock.Object, lf, session, pitProcessor, flagProcessor, cacheMux.Object, db.Object, dmProc);

        var dataReader = new TestDataReader("event-finish-with-reset.log");
        var data = dataReader.GetData();

        int finalCount = 0;
        session.FinalizedSession += () =>
        {
            finalCount++;
        };

        int count = 0;
        foreach (var cmd in data)
        {
            count++;
            //if (cmd.Contains("Finish"))
            {
                //Console.WriteLine("Finish command");
            }

            await processor.ProcessUpdate("rmonitor", cmd, 36);

            if (count % 10 == 0)
            {
                await processor.GetPayload();
            }
        }

        // Call get payload as if the 5 second timer would do for full status updates
        // First call to flush any remaining position updates in the mod 10 gap
        await processor.GetPayload();
        await processor.GetPayload();

        Assert.IsTrue(finalCount > 1);
    }
}
