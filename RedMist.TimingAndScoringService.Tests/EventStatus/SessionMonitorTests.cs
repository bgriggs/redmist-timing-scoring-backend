using BigMission.TestHelpers.Testing;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor;

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
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf, session, pitProcessor, null);

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
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf, session, pitProcessor, null);

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
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf, session, pitProcessor, null);

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
