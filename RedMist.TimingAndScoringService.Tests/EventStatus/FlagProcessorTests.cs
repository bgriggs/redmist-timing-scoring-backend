using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus.FlagData;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus;

[TestClass]
public class FlagProcessorTests
{
    private readonly DebugLoggerFactory lf = new();
    private readonly int testEventId = 1;
    private readonly int testSessionId = 100;

    private FlagProcessor CreateFlagProcessor()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockFactory = new Mock<IDbContextFactory<TsContext>>();
        mockFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TsContext(options));

        return new FlagProcessor(testEventId, mockFactory.Object, lf);
    }

    [TestMethod]
    public async Task StandardProgression_YellowGreenWhiteCheckered_Test()
    {
        // Flag states
        // Start 9am
        // Yellow - 2 minutes
        // Green - 1 hour
        // White - 2 minutes
        // Checkered 10 minutes

        var processor = CreateFlagProcessor();
        var startTime = new DateTime(2025, 1, 15, 9, 0, 0, DateTimeKind.Utc);

        var flagDurations = new List<FlagDuration>
        {
            // Yellow flag - 2 minutes
            new() 
            { 
                Flag = Flags.Yellow, 
                StartTime = startTime, 
                EndTime = startTime.AddMinutes(2) 
            },
            // Green flag - 1 hour
            new() 
            { 
                Flag = Flags.Green, 
                StartTime = startTime.AddMinutes(2), 
                EndTime = startTime.AddMinutes(62) 
            },
            // White flag - 2 minutes
            new() 
            { 
                Flag = Flags.White, 
                StartTime = startTime.AddMinutes(62), 
                EndTime = startTime.AddMinutes(64) 
            },
            // Checkered flag - 10 minutes (still ongoing)
            new() 
            { 
                Flag = Flags.Checkered, 
                StartTime = startTime.AddMinutes(64), 
                EndTime = null 
            }
        };

        // Act
        await processor.ProcessFlags(testSessionId, flagDurations, CancellationToken.None);

        // Assert
        var resultFlags = await processor.GetFlagsAsync(CancellationToken.None);
        
        Assert.HasCount(4, resultFlags);
        
        // Verify Yellow flag
        var yellowFlag = resultFlags.FirstOrDefault(f => f.Flag == Flags.Yellow);
        Assert.IsNotNull(yellowFlag, "Yellow flag should exist");
        Assert.AreEqual(startTime, yellowFlag.StartTime, "Yellow flag start time should match");
        Assert.AreEqual(startTime.AddMinutes(2), yellowFlag.EndTime, "Yellow flag end time should match");
        
        // Verify Green flag
        var greenFlag = resultFlags.FirstOrDefault(f => f.Flag == Flags.Green);
        Assert.IsNotNull(greenFlag, "Green flag should exist");
        Assert.AreEqual(startTime.AddMinutes(2), greenFlag.StartTime, "Green flag start time should match");
        Assert.AreEqual(startTime.AddMinutes(62), greenFlag.EndTime, "Green flag end time should match");
        
        // Verify White flag
        var whiteFlag = resultFlags.FirstOrDefault(f => f.Flag == Flags.White);
        Assert.IsNotNull(whiteFlag, "White flag should exist");
        Assert.AreEqual(startTime.AddMinutes(62), whiteFlag.StartTime, "White flag start time should match");
        Assert.AreEqual(startTime.AddMinutes(64), whiteFlag.EndTime, "White flag end time should match");
        
        // Verify Checkered flag (still ongoing)
        var checkeredFlag = resultFlags.FirstOrDefault(f => f.Flag == Flags.Checkered);
        Assert.IsNotNull(checkeredFlag, "Checkered flag should exist");
        Assert.AreEqual(startTime.AddMinutes(64), checkeredFlag.StartTime, "Checkered flag start time should match");
        Assert.IsNull(checkeredFlag.EndTime, "Checkered flag should still be ongoing (no end time)");
    }

    [TestMethod]
    public async Task MissingGaps_YellowGreenGapYellowWhiteCheckered_Test()
    {
        // Flag states
        // Start 9am
        // Yellow - 2 minutes
        // Green - 30 minutes
        // Gap - 5 minutes (no flag data)
        // Yellow - 25 minutes
        // White - 2 minutes
        // Checkered 10 minutes

        var processor = CreateFlagProcessor();
        var startTime = new DateTime(2025, 1, 15, 9, 0, 0, DateTimeKind.Utc);

        // First batch of flags (before the gap)
        var initialFlags = new List<FlagDuration>
        {
            // Yellow flag - 2 minutes
            new() 
            { 
                Flag = Flags.Yellow, 
                StartTime = startTime, 
                EndTime = startTime.AddMinutes(2) 
            },
            // Green flag - 30 minutes (ends before gap)
            new() 
            { 
                Flag = Flags.Green, 
                StartTime = startTime.AddMinutes(2), 
                EndTime = startTime.AddMinutes(32) 
            }
        };

        // Process initial flags
        await processor.ProcessFlags(testSessionId, initialFlags, CancellationToken.None);

        // Verify initial state
        var initialResult = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(2, initialResult);

        // Second batch of flags (after the gap)
        var laterFlags = new List<FlagDuration>
        {
            // Keep existing flags
            new() 
            { 
                Flag = Flags.Yellow, 
                StartTime = startTime, 
                EndTime = startTime.AddMinutes(2) 
            },
            new() 
            { 
                Flag = Flags.Green, 
                StartTime = startTime.AddMinutes(2), 
                EndTime = startTime.AddMinutes(32) 
            },
            // Yellow flag after gap - 25 minutes
            new() 
            { 
                Flag = Flags.Yellow, 
                StartTime = startTime.AddMinutes(37), // 5 minute gap after green ended
                EndTime = startTime.AddMinutes(62) 
            },
            // White flag - 2 minutes
            new() 
            { 
                Flag = Flags.White, 
                StartTime = startTime.AddMinutes(62), 
                EndTime = startTime.AddMinutes(64) 
            },
            // Checkered flag - ongoing
            new() 
            { 
                Flag = Flags.Checkered, 
                StartTime = startTime.AddMinutes(64), 
                EndTime = null 
            }
        };

        // Process flags after gap
        await processor.ProcessFlags(testSessionId, laterFlags, CancellationToken.None);

        // Assert final state
        var finalFlags = await processor.GetFlagsAsync(CancellationToken.None);
        
        Assert.HasCount(5, finalFlags);
        
        // Verify first Yellow flag
        var firstYellow = finalFlags.Where(f => f.Flag == Flags.Yellow && f.StartTime == startTime).FirstOrDefault();
        Assert.IsNotNull(firstYellow, "First yellow flag should exist");
        Assert.AreEqual(startTime.AddMinutes(2), firstYellow.EndTime, "First yellow flag end time should match");
        
        // Verify Green flag
        var greenFlag = finalFlags.FirstOrDefault(f => f.Flag == Flags.Green);
        Assert.IsNotNull(greenFlag, "Green flag should exist");
        Assert.AreEqual(startTime.AddMinutes(2), greenFlag.StartTime, "Green flag start time should match");
        Assert.AreEqual(startTime.AddMinutes(32), greenFlag.EndTime, "Green flag end time should match");
        
        // Verify second Yellow flag (after gap)
        var secondYellow = finalFlags.Where(f => f.Flag == Flags.Yellow && f.StartTime == startTime.AddMinutes(37)).FirstOrDefault();
        Assert.IsNotNull(secondYellow, "Second yellow flag should exist");
        Assert.AreEqual(startTime.AddMinutes(37), secondYellow.StartTime, "Second yellow flag start time should match");
        Assert.AreEqual(startTime.AddMinutes(62), secondYellow.EndTime, "Second yellow flag end time should match");
        
        // Verify White flag
        var whiteFlag = finalFlags.FirstOrDefault(f => f.Flag == Flags.White);
        Assert.IsNotNull(whiteFlag, "White flag should exist");
        Assert.AreEqual(startTime.AddMinutes(62), whiteFlag.StartTime, "White flag start time should match");
        Assert.AreEqual(startTime.AddMinutes(64), whiteFlag.EndTime, "White flag end time should match");
        
        // Verify Checkered flag (ongoing)
        var checkeredFlag = finalFlags.FirstOrDefault(f => f.Flag == Flags.Checkered);
        Assert.IsNotNull(checkeredFlag, "Checkered flag should exist");
        Assert.AreEqual(startTime.AddMinutes(64), checkeredFlag.StartTime, "Checkered flag start time should match");
        Assert.IsNull(checkeredFlag.EndTime, "Checkered flag should still be ongoing");
        
        // Verify the gap: there should be a 5-minute gap between green ending and second yellow starting
        Assert.AreEqual(5, (secondYellow.StartTime - greenFlag.EndTime!).Value.TotalMinutes, 
            "Should have a 5-minute gap between green end and second yellow start");
    }

    [TestMethod]
    public async Task ProcessFlags_UpdatesEndTime_WhenFlagCompletes_Test()
    {
        var processor = CreateFlagProcessor();
        var startTime = new DateTime(2025, 1, 15, 9, 0, 0, DateTimeKind.Utc);

        // First process: Yellow flag without end time (ongoing)
        var ongoingFlags = new List<FlagDuration>
        {
            new() 
            { 
                Flag = Flags.Yellow, 
                StartTime = startTime, 
                EndTime = null // Ongoing flag
            }
        };

        await processor.ProcessFlags(testSessionId, ongoingFlags, CancellationToken.None);

        var initialResult = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(1, initialResult);
        Assert.IsNull(initialResult[0].EndTime, "Flag should initially be ongoing");

        // Second process: Same flag now with end time
        var completedFlags = new List<FlagDuration>
        {
            new() 
            { 
                Flag = Flags.Yellow, 
                StartTime = startTime, 
                EndTime = startTime.AddMinutes(5) // Now completed
            }
        };

        await processor.ProcessFlags(testSessionId, completedFlags, CancellationToken.None);

        var finalResult = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(1, finalResult);
        Assert.IsNotNull(finalResult[0].EndTime, "Flag should now have end time");
        Assert.AreEqual(startTime.AddMinutes(5), finalResult[0].EndTime, "End time should match");
    }

    [TestMethod]
    public async Task ProcessFlags_SessionIdChange_ClearsAndReloads_Test()
    {
        var processor = CreateFlagProcessor();
        var startTime = new DateTime(2025, 1, 15, 9, 0, 0, DateTimeKind.Utc);

        // Process flags for first session
        var session1Flags = new List<FlagDuration>
        {
            new() 
            { 
                Flag = Flags.Green, 
                StartTime = startTime, 
                EndTime = startTime.AddMinutes(10) 
            }
        };

        await processor.ProcessFlags(testSessionId, session1Flags, CancellationToken.None);
        
        var session1Result = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(1, session1Result);

        // Change to new session
        var newSessionId = testSessionId + 1;
        var session2Flags = new List<FlagDuration>
        {
            new() 
            { 
                Flag = Flags.Yellow, 
                StartTime = startTime.AddHours(1), 
                EndTime = startTime.AddHours(1).AddMinutes(5) 
            }
        };

        await processor.ProcessFlags(newSessionId, session2Flags, CancellationToken.None);
        
        var session2Result = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(1, session2Result);
        Assert.AreEqual(Flags.Yellow, session2Result[0].Flag, "Session 2 should have Yellow flag");
        Assert.AreEqual(newSessionId, processor.SessionId, "Processor should track new session ID");
    }

    [TestMethod]
    public async Task ProcessFlags_HandlesMultipleFlagTypes_Test()
    {
        var processor = CreateFlagProcessor();
        var startTime = new DateTime(2025, 1, 15, 9, 0, 0, DateTimeKind.Utc);

        var mixedFlags = new List<FlagDuration>
        {
            new() { Flag = Flags.Green, StartTime = startTime, EndTime = startTime.AddMinutes(30) },
            new() { Flag = Flags.Yellow, StartTime = startTime.AddMinutes(30), EndTime = startTime.AddMinutes(35) },
            new() { Flag = Flags.Red, StartTime = startTime.AddMinutes(35), EndTime = startTime.AddMinutes(40) },
            new() { Flag = Flags.Green, StartTime = startTime.AddMinutes(40), EndTime = startTime.AddMinutes(70) },
            new() { Flag = Flags.White, StartTime = startTime.AddMinutes(70), EndTime = startTime.AddMinutes(72) },
            new() { Flag = Flags.Checkered, StartTime = startTime.AddMinutes(72), EndTime = null }
        };

        await processor.ProcessFlags(testSessionId, mixedFlags, CancellationToken.None);

        var result = await processor.GetFlagsAsync(CancellationToken.None);
        
        Assert.HasCount(6, result);
        
        // Verify we have all expected flag types
        Assert.IsTrue(result.Any(f => f.Flag == Flags.Green), "Should have Green flag");
        Assert.IsTrue(result.Any(f => f.Flag == Flags.Yellow), "Should have Yellow flag");
        Assert.IsTrue(result.Any(f => f.Flag == Flags.Red), "Should have Red flag");
        Assert.IsTrue(result.Any(f => f.Flag == Flags.White), "Should have White flag");
        Assert.IsTrue(result.Any(f => f.Flag == Flags.Checkered), "Should have Checkered flag");
        
        // Verify the ongoing checkered flag
        var checkeredFlag = result.FirstOrDefault(f => f.Flag == Flags.Checkered);
        Assert.IsNotNull(checkeredFlag, "Checkered flag should exist");
        Assert.IsNull(checkeredFlag.EndTime, "Checkered flag should be ongoing");
    }

    [TestMethod]
    public async Task ProcessFlags_AutoCompletePreviousFlag_WhenNewFlagStarts_Test()
    {
        var processor = CreateFlagProcessor();
        var yellowStartTime = new DateTime(2025, 8, 8, 9, 36, 54, DateTimeKind.Utc);
        var greenStartTime = new DateTime(2025, 8, 8, 10, 8, 9, DateTimeKind.Utc);

        // First process: Yellow flag (Flag 2) without end time (ongoing)
        var yellowFlags = new List<FlagDuration>
        {
            new() 
            { 
                Flag = Flags.Yellow, // Flag value 2
                StartTime = yellowStartTime, 
                EndTime = null // Ongoing flag
            }
        };

        await processor.ProcessFlags(67, yellowFlags, CancellationToken.None); // Using session 67 from the issue

        var initialResult = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(1, initialResult);
        Assert.IsNull(initialResult[0].EndTime, "Yellow flag should initially be ongoing");

        // Second process: Green flag (Flag 1) starts later - should auto-complete the Yellow flag
        // NOTE: We should only send the NEW flag, not repeat existing flags
        var greenFlags = new List<FlagDuration>
        {
            // New green flag
            new() 
            { 
                Flag = Flags.Green, // Flag value 1  
                StartTime = greenStartTime, 
                EndTime = null // New ongoing flag
            }
        };

        await processor.ProcessFlags(67, greenFlags, CancellationToken.None);

        var finalResult = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(2, finalResult);
        
        // Verify Yellow flag was auto-completed
        var yellowFlag = finalResult.FirstOrDefault(f => f.Flag == Flags.Yellow);
        Assert.IsNotNull(yellowFlag, "Yellow flag should exist");
        Assert.IsNotNull(yellowFlag.EndTime, "Yellow flag should now have end time");
        Assert.AreEqual(greenStartTime, yellowFlag.EndTime, "Yellow flag end time should be Green flag start time");
        
        // Verify Green flag is ongoing
        var greenFlag = finalResult.FirstOrDefault(f => f.Flag == Flags.Green);
        Assert.IsNotNull(greenFlag, "Green flag should exist");
        Assert.AreEqual(greenStartTime, greenFlag.StartTime, "Green flag start time should match");
        Assert.IsNull(greenFlag.EndTime, "Green flag should be ongoing");
    }

    [TestMethod]
    public async Task ProcessFlags_RealisticScenario_AutoCompleteWithIncrementalUpdates_Test()
    {
        var processor = CreateFlagProcessor();
        var sessionId = 67;
        var baseTime = new DateTime(2025, 8, 8, 9, 0, 0, DateTimeKind.Utc);

        // Scenario 1: Yellow flag starts at race beginning
        var yellowStart = baseTime.AddMinutes(5);
        await processor.ProcessFlags(sessionId,
        [
            new() { Flag = Flags.Yellow, StartTime = yellowStart, EndTime = null }
        ], CancellationToken.None);

        var result1 = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(1, result1, "Should have 1 flag after yellow");
        Assert.IsNull(result1[0].EndTime, "Yellow flag should be ongoing");

        // Scenario 2: Green flag starts, should auto-complete yellow
        var greenStart = baseTime.AddMinutes(10);
        await processor.ProcessFlags(sessionId,
        [
            new() { Flag = Flags.Green, StartTime = greenStart, EndTime = null }
        ], CancellationToken.None);

        var result2 = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(2, result2, "Should have 2 flags after green");

        var yellow = result2.First(f => f.Flag == Flags.Yellow);
        var green = result2.First(f => f.Flag == Flags.Green);

        Assert.IsNotNull(yellow.EndTime, "Yellow flag should be auto-completed");
        Assert.AreEqual(greenStart, yellow.EndTime, "Yellow should end when green starts");
        Assert.IsNull(green.EndTime, "Green flag should be ongoing");

        // Scenario 3: White flag starts, should auto-complete green
        var whiteStart = baseTime.AddMinutes(65);
        await processor.ProcessFlags(sessionId,
        [
            new() { Flag = Flags.White, StartTime = whiteStart, EndTime = null }
        ], CancellationToken.None);

        var result3 = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(3, result3, "Should have 3 flags after white");

        var updatedGreen = result3.First(f => f.Flag == Flags.Green);
        var white = result3.First(f => f.Flag == Flags.White);

        Assert.IsNotNull(updatedGreen.EndTime, "Green flag should be auto-completed");
        Assert.AreEqual(whiteStart, updatedGreen.EndTime, "Green should end when white starts");
        Assert.IsNull(white.EndTime, "White flag should be ongoing");

        // Scenario 4: Checkered flag starts, should auto-complete white
        var checkeredStart = baseTime.AddMinutes(67);
        await processor.ProcessFlags(sessionId,
        [
            new() { Flag = Flags.Checkered, StartTime = checkeredStart, EndTime = null }
        ], CancellationToken.None);

        var finalResult = await processor.GetFlagsAsync(CancellationToken.None);
        Assert.HasCount(4, finalResult, "Should have 4 flags after checkered");
        
        var updatedWhite = finalResult.First(f => f.Flag == Flags.White);
        var checkered = finalResult.First(f => f.Flag == Flags.Checkered);
        
        Assert.IsNotNull(updatedWhite.EndTime, "White flag should be auto-completed");
        Assert.AreEqual(checkeredStart, updatedWhite.EndTime, "White should end when checkered starts");
        Assert.IsNull(checkered.EndTime, "Checkered flag should be ongoing");

        // Verify complete timeline
        var orderedFlags = finalResult.OrderBy(f => f.StartTime).ToList();
        Assert.AreEqual(Flags.Yellow, orderedFlags[0].Flag);
        Assert.AreEqual(Flags.Green, orderedFlags[1].Flag);
        Assert.AreEqual(Flags.White, orderedFlags[2].Flag);
        Assert.AreEqual(Flags.Checkered, orderedFlags[3].Flag);

        // Verify no gaps in the timeline
        Assert.AreEqual(orderedFlags[0].EndTime, orderedFlags[1].StartTime, "No gap between yellow and green");
        Assert.AreEqual(orderedFlags[1].EndTime, orderedFlags[2].StartTime, "No gap between green and white");
        Assert.AreEqual(orderedFlags[2].EndTime, orderedFlags[3].StartTime, "No gap between white and checkered");
    }
}
