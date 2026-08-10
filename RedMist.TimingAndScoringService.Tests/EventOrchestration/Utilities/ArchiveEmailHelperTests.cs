using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventOrchestration.Utilities;
using RedMist.EventProcessor.Tests.Utilities;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;

/// <summary>
/// The failure alert is the only signal an operator gets when the nightly archive breaks, so the
/// branches that decide what it says are covered here.
/// </summary>
[TestClass]
public class ArchiveEmailHelperTests
{
    private IDbContextFactory<TsContext> dbFactory = null!;
    private ArchiveEmailHelper helper = null!;
    private readonly List<(string Subject, string Body, string To, string From)> sent = [];
    private Exception? sendError;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"ArchiveEmailHelperTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);

        helper = new ArchiveEmailHelper(new Mock<ILogger>().Object, dbFactory, emailHelper: null,
            (subject, body, to, from) =>
            {
                if (sendError is not null) throw sendError;
                sent.Add((subject, body, to, from));
                return Task.CompletedTask;
            });
    }

    [TestMethod]
    public async Task SendArchiveFailureEmailAsync_KnownEvent_IncludesEventNameAndEndDateInBody()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event
            {
                Id = 42,
                OrganizationId = 7,
                Name = "Summer Enduro",
                EndDate = new DateTime(2026, 7, 4, 18, 30, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        await helper.SendArchiveFailureEmailAsync("Failed to archive laps", 42, retryCount: 0, exception: null);

        var email = sent.Single();
        Assert.AreEqual("Red Mist Archive Failure - Event 42", email.Subject);
        Assert.AreEqual("support@redmist.racing", email.To);
        Assert.AreEqual("noreply@redmist.racing", email.From);
        StringAssert.Contains(email.Body, "Failed to archive laps");
        StringAssert.Contains(email.Body, "Event ID: 42");
        StringAssert.Contains(email.Body, "Summer Enduro");
        // The time separator in the rendered date is culture dependent, so only the date is asserted.
        StringAssert.Contains(email.Body, "End Date: 2026-07-04");
    }

    [TestMethod]
    public void Constructor_WithNoEmailHelperAndNoSendDelegate_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ArchiveEmailHelper(new Mock<ILogger>().Object, dbFactory, emailHelper: null));
    }

    [TestMethod]
    public async Task SendArchiveFailureEmailAsync_EventNotInDatabase_SaysSo()
    {
        await helper.SendArchiveFailureEmailAsync("Failed to archive laps", 999, retryCount: 0, exception: null);

        StringAssert.Contains(sent.Single().Body, "Event ID: 999 (Event not found in database)");
    }

    [TestMethod]
    public async Task SendArchiveFailureEmailAsync_NoEventId_UsesGenericSubjectAndNoEventInformation()
    {
        await helper.SendArchiveFailureEmailAsync("Archive process failed after all retry attempts", null,
            retryCount: 3, exception: null);

        var email = sent.Single();
        Assert.AreEqual("Red Mist Archive Failure", email.Subject);
        StringAssert.Contains(email.Body, "<strong>Event Information:</strong> N/A");
    }

    [TestMethod]
    public async Task SendArchiveFailureEmailAsync_RetriesExhausted_ReportsTheRetryCount()
    {
        await helper.SendArchiveFailureEmailAsync("failed", null, retryCount: 3, exception: null);

        StringAssert.Contains(sent.Single().Body, "<strong>Retry Attempts:</strong> 3");
    }

    [TestMethod]
    public async Task SendArchiveFailureEmailAsync_NoRetries_OmitsTheRetrySection()
    {
        await helper.SendArchiveFailureEmailAsync("failed", null, retryCount: 0, exception: null);

        Assert.DoesNotContain("Retry Attempts", sent.Single().Body);
    }

    [TestMethod]
    public async Task SendArchiveFailureEmailAsync_SyntheticExceptionWithNoStackTrace_ExplainsItWasNeverThrown()
    {
        // The archive service builds diagnostic exceptions it never throws; a blank stack trace
        // in the alert would otherwise look like data was lost.
        var synthetic = new Exception("Failed to archive laps for event 5, session 2.");

        await helper.SendArchiveFailureEmailAsync("Failed to archive laps", null, 0, synthetic);

        var body = sent.Single().Body;
        StringAssert.Contains(body, "System.Exception");
        StringAssert.Contains(body, "Failed to archive laps for event 5, session 2.");
        StringAssert.Contains(body, "was not thrown");
    }

    [TestMethod]
    public async Task SendArchiveFailureEmailAsync_ThrownExceptionWithInner_IncludesBothExceptions()
    {
        Exception captured;
        try
        {
            try
            {
                throw new InvalidOperationException("inner boom");
            }
            catch (Exception inner)
            {
                throw new ApplicationException("outer boom", inner);
            }
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        await helper.SendArchiveFailureEmailAsync("Failed to archive X2 data", 7, 0, captured);

        var body = sent.Single().Body;
        StringAssert.Contains(body, "outer boom");
        StringAssert.Contains(body, "Inner Exception");
        StringAssert.Contains(body, "System.InvalidOperationException");
        StringAssert.Contains(body, "inner boom");
        Assert.DoesNotContain("Stack trace not available", body, "Both exceptions were thrown so both have stack traces");
    }

    [TestMethod]
    public async Task SendArchiveFailureEmailAsync_SendFails_DoesNotPropagate()
    {
        sendError = new InvalidOperationException("smtp unavailable");

        await helper.SendArchiveFailureEmailAsync("failed", null, 0, null);

        Assert.IsEmpty(sent);
    }
}
