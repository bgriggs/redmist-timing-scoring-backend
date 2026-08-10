using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventOrchestration.Utilities;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.X2;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;

/// <summary>
/// Purging is destructive and irreversible, so these pin down which rows go and which stay - but only
/// on one of the two code paths <see cref="PurgeUtilities"/> has.
///
/// Every delete method tries <c>ExecuteDeleteAsync</c> first and catches
/// <c>InvalidOperationException ... Contains("ExecuteDelete")</c> to fall back to a load-and-RemoveRange
/// pass over a second, fully duplicated list of the same tables. Production runs on Npgsql and therefore
/// takes the <c>ExecuteDeleteAsync</c> path; EF InMemory does not support it, so <em>every row-level test
/// in this class exercises the fallback</em>. The shipping path is not covered here and cannot be without
/// a real database.
///
/// <see cref="DeleteAllEventDataAsync_BothBranchesTouchTheSameDbSets"/> is what stands in for that: it
/// reads the compiled method and checks the two duplicated lists have not drifted apart, which is the
/// failure the row-level tests would sail straight past.
/// </summary>
[TestClass]
public class PurgeUtilitiesTests
{
    /// <summary>
    /// The tables <see cref="PurgeUtilities.DeleteAllEventDataAsync"/> clears, in the order the
    /// <c>ExecuteDeleteAsync</c> chain visits them (children first, the event itself last). Both of the
    /// method's duplicated delete lists have to name every one of these.
    /// </summary>
    private static readonly string[] PurgedDbSets =
    [
        nameof(TsContext.CarLapLogs),
        nameof(TsContext.CarLastLaps),
        nameof(TsContext.CompetitorMetadata),
        nameof(TsContext.EventStatusLogs),
        nameof(TsContext.FlagLog),
        nameof(TsContext.SessionResults),
        nameof(TsContext.Sessions),
        nameof(TsContext.X2Loops),
        nameof(TsContext.X2Passings),
        nameof(TsContext.Events),
    ];

    private IDbContextFactory<TsContext> dbFactory = null!;
    private PurgeUtilities purge = null!;

    [TestInitialize]
    public void Setup()
    {
        dbFactory = CreateFactory($"PurgeUtilitiesTests_{Guid.NewGuid()}");
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        purge = new PurgeUtilities(loggerFactory.Object, dbFactory);
    }

    /// <summary>
    /// The InMemory provider has no transactions; without this the purge would fail before doing any work.
    /// </summary>
    internal static IDbContextFactory<TsContext> CreateFactory(string databaseName)
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    [TestMethod]
    public async Task DeleteEventStatusLogsFromDatabaseAsync_RemovesOnlyTheTargetEventsLogs()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 1, SessionId = 1, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 1, SessionId = 2, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 2, SessionId = 1, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await purge.DeleteEventStatusLogsFromDatabaseAsync(1, totalLogs: 2, CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await check.EventStatusLogs.CountAsync(e => e.EventId == 1));
        Assert.AreEqual(1, await check.EventStatusLogs.CountAsync(e => e.EventId == 2));
    }

    [TestMethod]
    public async Task DeleteLapsFromDatabaseAsync_RemovesOnlyTheTargetEventAndSession()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.CarLapLogs.Add(NewLap(eventId: 1, sessionId: 1));
            db.CarLapLogs.Add(NewLap(eventId: 1, sessionId: 2));
            db.CarLapLogs.Add(NewLap(eventId: 2, sessionId: 1));
            await db.SaveChangesAsync();
        }

        await purge.DeleteLapsFromDatabaseAsync(eventId: 1, sessionId: 1, totalLaps: 1, CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await check.CarLapLogs.CountAsync(l => l.EventId == 1 && l.SessionId == 1));
        Assert.AreEqual(1, await check.CarLapLogs.CountAsync(l => l.EventId == 1 && l.SessionId == 2));
        Assert.AreEqual(1, await check.CarLapLogs.CountAsync(l => l.EventId == 2));
    }

    [TestMethod]
    public async Task DeleteAllEventDataAsync_RemovesEveryChildTableAndTheEventItself()
    {
        await SeedFullEventAsync(eventId: 1);

        await purge.DeleteAllEventDataAsync(1, CancellationToken.None);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.IsEmpty(await db.CarLapLogs.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.CarLastLaps.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.CompetitorMetadata.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.EventStatusLogs.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.FlagLog.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.SessionResults.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.Sessions.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.X2Loops.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.X2Passings.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.Events.Where(e => e.Id == 1).ToListAsync());
    }

    [TestMethod]
    public async Task DeleteAllEventDataAsync_LeavesOtherEventsIntact()
    {
        await SeedFullEventAsync(eventId: 1);
        await SeedFullEventAsync(eventId: 2);

        await purge.DeleteAllEventDataAsync(1, CancellationToken.None);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.HasCount(1, await db.CarLapLogs.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.CarLastLaps.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.CompetitorMetadata.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.EventStatusLogs.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.FlagLog.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.SessionResults.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.Sessions.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.X2Loops.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.X2Passings.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.Events.Where(e => e.Id == 2).ToListAsync());
    }

    /// <summary>
    /// The two delete lists inside <see cref="PurgeUtilities.DeleteAllEventDataAsync"/> are a copy of each
    /// other, and nothing in the row-level tests can tell them apart: those all run the fallback, so a table
    /// added to only the <c>ExecuteDeleteAsync</c> chain (the one production actually uses) or only to the
    /// fallback leaves every one of them green while an event is left half-purged in production.
    ///
    /// This reads the compiled method instead and lists every <see cref="TsContext"/> DbSet fetch it
    /// contains, in IL order. The <c>ExecuteDeleteAsync</c> chain comes first and fetches each table once;
    /// the fallback follows and fetches each twice (the <c>Where</c> and the <c>RemoveRange</c>), so every
    /// table is fetched exactly three times. A table in one list but not the other breaks that count.
    /// </summary>
    [TestMethod]
    public void DeleteAllEventDataAsync_BothBranchesTouchTheSameDbSets()
    {
        var references = DbSetReferences(
            typeof(PurgeUtilities).GetMethod(nameof(PurgeUtilities.DeleteAllEventDataAsync))!);

        CollectionAssert.AreEqual(PurgedDbSets, references.Distinct().ToArray(),
            "The tables the purge touches, or the order it touches them in, changed. Children must be deleted "
            + "before the event row they hang off, and any change has to be made to both the ExecuteDeleteAsync "
            + "chain and the InMemory fallback. Update PurgedDbSets once both are right.");

        // Hard-coded rather than derived from one of the tables: comparing the tables against each other
        // would go green if a whole branch were deleted, which is the regression this exists to catch.
        const int perTable = 3;
        foreach (var (table, count) in references.GroupBy(r => r).Select(g => (g.Key, g.Count())).OrderBy(r => r.Key))
        {
            Assert.AreEqual(perTable, count,
                $"{table} is fetched {count} time(s) rather than {perTable}: once by the ExecuteDeleteAsync chain "
                + "and twice by the fallback. Either it is missing from one of the two duplicated delete lists, or "
                + "one of the lists is gone entirely.");
        }
    }

    /// <summary>
    /// Every <see cref="TsContext"/> DbSet fetch in <paramref name="method"/>, in IL order and with repeats.
    /// Reads the IL of the async method's state machine, since both delete lists are compiled into the
    /// one <c>MoveNext</c> and there is no other way to see the branch that never runs under InMemory.
    /// </summary>
    private static List<string> DbSetReferences(MethodInfo method)
    {
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        Assert.IsNotNull(stateMachine, $"{method.Name} is no longer an async method.");
        var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(moveNext);
        var il = moveNext.GetMethodBody()?.GetILAsByteArray();
        Assert.IsNotNull(il, "The method body was not readable.");

        var references = new List<string>();
        foreach (var token in CallTokens(il))
        {
            MethodBase? callee;
            try
            {
                callee = moveNext.Module.ResolveMethod(token);
            }
            catch (ArgumentException)
            {
                // Generic instantiations (Where<T>, ToListAsync<T>, ...) need a context this does not
                // have; none of them are TsContext members, so skipping them loses nothing.
                continue;
            }

            if (callee?.DeclaringType != typeof(TsContext) || !callee.Name.StartsWith("get_", StringComparison.Ordinal))
                continue;

            references.Add(callee.Name["get_".Length..]);
        }
        return references;
    }

    /// <summary>
    /// Walks a method body and yields the metadata token of every <c>call</c>/<c>callvirt</c>. Instruction
    /// lengths come from <see cref="OpCodes"/> itself rather than a hand-written table.
    /// </summary>
    private static IEnumerable<int> CallTokens(byte[] il)
    {
        var operandSizes = OperandSizes();
        int i = 0;
        while (i < il.Length)
        {
            var offset = i;
            short opCode = il[i];
            if (il[i] == 0xFE)
            {
                opCode = (short)((0xFE << 8) | il[i + 1]);
                i += 2;
            }
            else
            {
                i++;
            }

            if (!operandSizes.TryGetValue(opCode, out var operandSize))
                throw new InvalidOperationException($"Unrecognized IL opcode 0x{opCode:X4} at offset {offset}.");

            if (operandSize < 0) // switch: a count followed by that many 4 byte targets
            {
                i += 4 + (4 * BitConverter.ToInt32(il, i));
                continue;
            }

            if (opCode == OpCodes.Call.Value || opCode == OpCodes.Callvirt.Value)
                yield return BitConverter.ToInt32(il, i);

            i += operandSize;
        }
    }

    /// <summary>Operand byte count per opcode; -1 marks the variable-length <c>switch</c>.</summary>
    private static Dictionary<short, int> OperandSizes()
    {
        var sizes = new Dictionary<short, int>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
                continue;

            sizes[op.Value] = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                    or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                    or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => -1,
                _ => throw new NotSupportedException($"Unhandled operand type {op.OperandType} on {op.Name}."),
            };
        }
        return sizes;
    }

    /// <summary>
    /// The purge opens its transaction <em>before</em> the try block, so a database that refuses one fails
    /// on the very first statement: "deletes nothing" is true by construction here, and the rollback in the
    /// catch is never reached. The assertion below is therefore only guarding that the failure is not
    /// swallowed - a genuine partial-delete-then-rollback needs a real database.
    /// </summary>
    [TestMethod]
    public async Task DeleteAllEventDataAsync_TransactionCannotStart_ThrowsAndDeletesNothing()
    {
        // A factory without the transaction warning suppressed stands in for a database that
        // refuses the transaction; the purge must fail loudly rather than half-delete an event.
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"PurgeUtilitiesTests_NoTx_{Guid.NewGuid()}")
            .Options;
        var strictFactory = new TestDbContextFactory(options);
        await using (var db = await strictFactory.CreateDbContextAsync())
        {
            db.CarLapLogs.Add(NewLap(eventId: 1, sessionId: 1));
            await db.SaveChangesAsync();
        }
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        var strictPurge = new PurgeUtilities(loggerFactory.Object, strictFactory);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => strictPurge.DeleteAllEventDataAsync(1, CancellationToken.None));

        await using var check = await strictFactory.CreateDbContextAsync();
        Assert.HasCount(1, await check.CarLapLogs.Where(e => e.EventId == 1).ToListAsync());
    }

    private static CarLapLog NewLap(int eventId, int sessionId) => new()
    {
        EventId = eventId,
        SessionId = sessionId,
        CarNumber = "5",
        Timestamp = DateTime.UtcNow,
        LapNumber = 1,
        Flag = 1,
        LapData = "{}"
    };

    private async Task SeedFullEventAsync(int eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Id = eventId, OrganizationId = 7, Name = $"Event {eventId}" });
        db.CarLapLogs.Add(NewLap(eventId, sessionId: 1));
        db.CarLastLaps.Add(new CarLastLap { EventId = eventId, SessionId = 1, CarNumber = "5", LastLapNumber = 1, LastLapTimestamp = DateTime.UtcNow });
        db.CompetitorMetadata.Add(new CompetitorMetadata { EventId = eventId, CarNumber = "5", LastUpdated = DateTime.UtcNow });
        db.EventStatusLogs.Add(new EventStatusLog { EventId = eventId, SessionId = 1, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
        db.FlagLog.Add(new FlagLog { EventId = eventId, SessionId = 1, Flag = Flags.Green, StartTime = DateTime.UtcNow });
        db.SessionResults.Add(new SessionResult { EventId = eventId, SessionId = 1, Start = DateTime.UtcNow });
        db.Sessions.Add(new Session { Id = 1, EventId = eventId, Name = "Race" });
        db.X2Loops.Add(new Loop { OrganizationId = 7, EventId = eventId, Id = 1, Name = "L1" });
        db.X2Passings.Add(new Passing { OrganizationId = 7, EventId = eventId, Id = 1, LoopId = 1 });
        await db.SaveChangesAsync();
    }
}
