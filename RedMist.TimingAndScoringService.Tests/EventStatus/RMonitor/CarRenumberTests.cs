using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Services;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.PipelineBlocks;
using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;

namespace RedMist.EventProcessor.Tests.EventStatus.RMonitor;

/// <summary>
/// A timing operator renumbering a car mid-session (216 to 216x) sends only an $A under the new number.
/// The transponder stays with the car, so the existing entry must move rather than a second car appear.
/// </summary>
[TestClass]
public class CarRenumberTests
{
    private RMonitorDataProcessor _processor = null!;
    private SessionContext _sessionContext = null!;
    private Mock<IClientProxy> _mockClientProxy = null!;
    private FakeTimeProvider _time = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        _mockClientProxy = new Mock<IClientProxy>();
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        var hubContext = new Mock<IHubContext<StatusHub>>();
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var dbContextFactory = CreateDbContextFactory();
        _sessionContext = new SessionContext(config, dbContextFactory, loggerFactory.Object, new Mock<ICarLapHistoryService>().Object);

        var resetProcessor = new ResetProcessor(_sessionContext, hubContext.Object, loggerFactory.Object);
        var startingPositionProcessor = new StartingPositionProcessor(_sessionContext, loggerFactory.Object, dbContextFactory);
        _time = new FakeTimeProvider();
        _processor = new RMonitorDataProcessor(loggerFactory.Object, _sessionContext, resetProcessor, startingPositionProcessor,
            new Mock<IMediator>().Object, _time);
    }

    private Task Send(string data) =>
        _processor.ProcessAsync(new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE, data, 1, DateTime.Now), _sessionContext);

    private static string A(string number, uint transponder, string first = "John") =>
        $"$A,\"{number}\",\"{number}\",{transponder},\"{first}\",\"Smith\",\"USA\",1";

    private List<string?> CarNumbers() => [.. _sessionContext.SessionState.CarPositions.Select(c => c.Number)];

    [TestMethod]
    public async Task NewNumberWithSameTransponder_MovesTheExistingCar()
    {
        // Arrange
        await Send(A("216", 13693568));
        await Send("$G,3,\"216\",7,\"00:20:00.000\"");
        var car = _sessionContext.GetCarByNumber("216");

        // Act
        await Send(A("216x", 13693568));

        // Assert - one car, the same object, now under the new number with its laps kept
        CollectionAssert.AreEqual(new[] { "216x" }, CarNumbers());
        Assert.AreSame(car, _sessionContext.GetCarByNumber("216x"));
        Assert.IsNull(_sessionContext.GetCarByNumber("216"));
        Assert.AreEqual(7, car!.LastLapCompleted);
        Assert.AreEqual(3, car.OverallPosition);
        Assert.AreEqual("216x", _sessionContext.GetCarNumberForTransponder(13693568));
        CollectionAssert.AreEqual(new[] { "216x" }, _sessionContext.SessionState.EventEntries.Select(e => e.Number).ToList());
    }

    [TestMethod]
    public async Task RenumberedThenCorrectedBack_LeavesOneCar()
    {
        // Arrange - 112 mistakenly changed to 112x, then corrected
        await Send(A("112", 15548088));

        // Act
        await Send(A("112x", 15548088));
        await Send(A("112", 15548088));

        // Assert
        CollectionAssert.AreEqual(new[] { "112" }, CarNumbers());
        CollectionAssert.AreEqual(new[] { "112" }, _sessionContext.SessionState.EventEntries.Select(e => e.Number).ToList());
    }

    [TestMethod]
    public async Task RenumberedCar_KeepsTakingLapsUnderNewNumber()
    {
        // Arrange
        await Send(A("216", 13693568));
        await Send("$G,3,\"216\",7,\"00:20:00.000\"");
        await Send(A("216x", 13693568));

        // Act
        await Send("$G,2,\"216x\",8,\"00:22:40.000\"");

        // Assert
        var car = _sessionContext.GetCarByNumber("216x");
        Assert.AreEqual(8, car!.LastLapCompleted);
        Assert.AreEqual(2, car.OverallPosition);
        Assert.HasCount(1, _sessionContext.SessionState.CarPositions);
    }

    [TestMethod]
    public async Task Renumber_TellsClientsToReset()
    {
        // Arrange
        await Send(A("216", 13693568));
        _mockClientProxy.Invocations.Clear();

        // Act
        await Send(A("216x", 13693568));

        // Assert
        _mockClientProxy.Verify(c => c.SendCoreAsync("ReceiveReset", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task DriverChangeUnderSameNumber_DoesNotResetClients()
    {
        // Arrange
        await Send(A("216", 13693568));
        _mockClientProxy.Invocations.Clear();

        // Act
        await Send(A("216", 13693568, first: "Jane"));

        // Assert
        _mockClientProxy.Verify(c => c.SendCoreAsync("ReceiveReset", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        CollectionAssert.AreEqual(new[] { "216" }, CarNumbers());
    }

    [TestMethod]
    public async Task DifferentTransponders_AreSeparateCars()
    {
        // Act
        await Send(A("216", 13693568) + "\n" + A("217", 13693569));

        // Assert
        CollectionAssert.AreEquivalent(new[] { "216", "217" }, CarNumbers());
    }

    [TestMethod]
    public async Task NoTransponder_IsNeverTreatedAsRenumber()
    {
        // Act
        await Send(A("216", 0) + "\n" + A("217", 0));

        // Assert
        CollectionAssert.AreEquivalent(new[] { "216", "217" }, CarNumbers());
    }

    [TestMethod]
    public async Task NewNumberCreatedByCompFirst_KeepsTheTimedCar()
    {
        // Arrange - a $COMP for the new number, which carries no transponder, arrives before its $A
        await Send(A("216", 13693568));
        await Send("$G,3,\"216\",7,\"00:20:00.000\"");
        var timedCar = _sessionContext.GetCarByNumber("216");
        await Send("$COMP,\"216x\",\"216x\",1,\"John\",\"Smith\",\"USA\",\"Team\"");
        Assert.HasCount(2, _sessionContext.SessionState.CarPositions);

        // Act
        await Send(A("216x", 13693568));

        // Assert - the car with the laps survives under the new number
        CollectionAssert.AreEqual(new[] { "216x" }, CarNumbers());
        Assert.AreSame(timedCar, _sessionContext.GetCarByNumber("216x"));
        Assert.AreEqual(7, timedCar!.LastLapCompleted);
        Assert.AreEqual(3, timedCar.OverallPosition);
    }

    [TestMethod]
    public async Task UnchangedRaceRecordAfterRenumber_IsNotAChange()
    {
        // Arrange - the car's race record moved with it, so the timing system repeating the same
        // position and laps under the new number is recognized as nothing new
        await Send(A("216", 13693568));
        await Send("$G,3,\"216\",7,\"00:20:00.000\"");
        await Send(A("216x", 13693568));

        // Act
        var result = await _processor.ProcessAsync(new TimingMessage(Backend.Shared.Consts.RMONITOR_TYPE,
            "$G,3,\"216x\",7,\"00:20:01.000\"", 1, DateTime.Now), _sessionContext);

        // Assert
        Assert.IsEmpty(result!.CarPatches);
        Assert.AreEqual(7, _sessionContext.GetCarByNumber("216x")!.LastLapCompleted);
    }

    [TestMethod]
    public async Task RenumberWithoutComp_KeepsTeamName()
    {
        // Arrange
        await Send(A("216", 13693568) + "\n" + "$COMP,\"216\",\"216\",1,\"John\",\"Smith\",\"USA\",\"Team Red\"");

        // Act - only the $A is sent for the new number
        await Send(A("216x", 13693568));

        // Assert
        var entry = _sessionContext.SessionState.EventEntries.Single();
        Assert.AreEqual("216x", entry.Number);
        Assert.AreEqual("Team Red", entry.Team);
    }

    [TestMethod]
    public async Task TransponderSwappedBetweenCars_KeepsBothCars()
    {
        // Arrange - transponders entered the wrong way round
        await Send(A("12", 100) + "\n" + A("14", 200));
        await Send("$G,1,\"14\",5,\"00:10:00.000\"");
        _mockClientProxy.Invocations.Clear();

        // Act - and swapped
        await Send(A("12", 200));
        await Send(A("14", 100));

        // Assert
        CollectionAssert.AreEquivalent(new[] { "12", "14" }, CarNumbers());
        Assert.AreEqual(5, _sessionContext.GetCarByNumber("14")!.LastLapCompleted);
        _mockClientProxy.Verify(c => c.SendCoreAsync("ReceiveReset", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task TransponderClearedThenGivenToNewCar_KeepsTheOldCar()
    {
        // Arrange - car 12's transponder field emptied, then the transponder entered on a new car
        await Send(A("12", 100));
        await Send("$A,\"12\",\"12\",,\"John\",\"Smith\",\"USA\",1");

        // Act
        await Send(A("14", 100));

        // Assert
        CollectionAssert.AreEquivalent(new[] { "12", "14" }, CarNumbers());
    }

    [TestMethod]
    public async Task SharedTransponderInInitListing_KeepsBothCars()
    {
        // Arrange - the listing after an $I is complete, so two cars sharing a transponder there are
        // a data entry mistake, not a renumber

        // Act
        await Send(string.Join("\n", "$I,\"16:36:08.000\",\"12 jan 01\"", A("12", 100), A("14", 100)));

        // Assert
        CollectionAssert.AreEquivalent(new[] { "12", "14" }, CarNumbers());
    }

    [TestMethod]
    public async Task Renumber_MovesStartingPosition()
    {
        // Arrange
        await Send(A("216", 13693568));
        _sessionContext.SetStartingPosition("216", 5);

        // Act
        await Send(A("216x", 13693568));

        // Assert
        Assert.AreEqual(5, _sessionContext.GetStartingPosition("216x"));
        Assert.IsNull(_sessionContext.GetStartingPosition("216"));
    }

    [TestMethod]
    public async Task TwoListedCarsSharingATransponder_StopFlippingAndAreKeptApart()
    {
        // Arrange - late entry 99 is given car 12's transponder while 12 is still listed. Orbits
        // re-lists every competitor every few seconds, so both keep arriving.
        await Send(A("12", 100));
        await Send("$G,1,\"12\",5,\"00:10:00.000\"");
        var car12 = _sessionContext.GetCarByNumber("12");

        // Act - three listing cycles
        for (int i = 0; i < 3; i++)
        {
            await Send(A("99", 100));
            _time.Advance(TimeSpan.FromSeconds(2));
            await Send(A("12", 100));
            _time.Advance(TimeSpan.FromSeconds(3));
        }

        // Assert - both cars listed, 12 keeps its laps, and clients reset only for the first two moves
        CollectionAssert.AreEquivalent(new[] { "12", "99" }, CarNumbers());
        Assert.AreSame(car12, _sessionContext.GetCarByNumber("12"));
        Assert.AreEqual(5, car12!.LastLapCompleted);
        Assert.AreEqual(0, _sessionContext.GetCarByNumber("99")!.LastLapCompleted);
        _mockClientProxy.Verify(c => c.SendCoreAsync("ReceiveReset", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task CorrectedThenRenumberedAgainLater_IsStillARenumber()
    {
        // Arrange - 112 mistakenly changed to 112x and corrected
        await Send(A("112", 15548088));
        await Send(A("112x", 15548088));
        await Send(A("112", 15548088));

        // Act - a genuine renumber well after the correction
        _time.Advance(TimeSpan.FromMinutes(5));
        await Send(A("112b", 15548088));

        // Assert
        CollectionAssert.AreEqual(new[] { "112b" }, CarNumbers());
    }

    [TestMethod]
    public async Task TimedCarWithTransponderCleared_IsNotReplaced()
    {
        // Arrange - car 14 has been timed, then its transponder field is emptied and set to car 12's
        await Send(A("12", 100) + "\n" + A("14", 200));
        await Send("$G,1,\"12\",5,\"00:10:00.000\"\n$G,2,\"14\",9,\"00:10:05.000\"");
        var car14 = _sessionContext.GetCarByNumber("14");
        await Send("$A,\"14\",\"14\",,\"Jane\",\"Smith\",\"USA\",1");

        // Act
        await Send(A("14", 100));

        // Assert
        CollectionAssert.AreEquivalent(new[] { "12", "14" }, CarNumbers());
        Assert.AreSame(car14, _sessionContext.GetCarByNumber("14"));
        Assert.AreEqual(9, car14!.LastLapCompleted);
        Assert.AreEqual(5, _sessionContext.GetCarByNumber("12")!.LastLapCompleted);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        return new TestDbContextFactory(optionsBuilder.Options);
    }
}
