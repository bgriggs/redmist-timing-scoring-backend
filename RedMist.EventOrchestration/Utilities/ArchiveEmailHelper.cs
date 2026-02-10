using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;

namespace RedMist.EventOrchestration.Utilities;

public class ArchiveEmailHelper
{
    private readonly ILogger logger;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly EmailHelper emailHelper;

    public ArchiveEmailHelper(ILogger logger, IDbContextFactory<TsContext> tsContext, EmailHelper emailHelper)
    {
        this.logger = logger;
        this.tsContext = tsContext;
        this.emailHelper = emailHelper;
    }

    public async Task SendArchiveFailureEmailAsync(string failureReason, int? eventId, int retryCount, Exception? exception)
    {
        try
        {
            var eventInfo = "N/A";
            if (eventId.HasValue)
            {
                try
                {
                    await using var dbContext = await tsContext.CreateDbContextAsync();
                    var ev = await dbContext.Events.FirstOrDefaultAsync(e => e.Id == eventId.Value);
                    if (ev != null)
                    {
                        eventInfo = $"Event ID: {eventId.Value}, Name: {ev.Name}, End Date: {ev.EndDate:yyyy-MM-dd HH:mm:ss} UTC";
                    }
                    else
                    {
                        eventInfo = $"Event ID: {eventId.Value} (Event not found in database)";
                    }
                }
                catch
                {
                    eventInfo = $"Event ID: {eventId.Value} (Unable to retrieve event details)";
                }
            }

            var exceptionDetails = "";
            if (exception != null)
            {
                var stackTrace = string.IsNullOrWhiteSpace(exception.StackTrace)
                    ? "<em>Stack trace not available. This exception was created for diagnostic purposes and was not thrown. Check the exception message for details and review the EventOrchestration logs for more information.</em>"
                    : exception.StackTrace;

                exceptionDetails = $@"
                    <h3>Exception Details</h3>
                    <p><strong>Exception Type:</strong> {exception.GetType().FullName}</p>
                    <p><strong>Exception Message:</strong> {exception.Message}</p>
                    <p><strong>Stack Trace:</strong></p>
                    <pre style=""background-color: #f4f4f4; padding: 10px; border: 1px solid #ddd; overflow-x: auto;"">{stackTrace}</pre>";

                if (exception.InnerException != null)
                {
                    var innerStackTrace = string.IsNullOrWhiteSpace(exception.InnerException.StackTrace)
                        ? "<em>Stack trace not available</em>"
                        : exception.InnerException.StackTrace;

                    exceptionDetails += $@"
                    <h4>Inner Exception</h4>
                    <p><strong>Type:</strong> {exception.InnerException.GetType().FullName}</p>
                    <p><strong>Message:</strong> {exception.InnerException.Message}</p>
                    <p><strong>Stack Trace:</strong></p>
                    <pre style=""background-color: #f4f4f4; padding: 10px; border: 1px solid #ddd; overflow-x: auto;"">{innerStackTrace}</pre>";
                }
            }

            var subject = eventId.HasValue
                ? $"Red Mist Archive Failure - Event {eventId.Value}"
                : "Red Mist Archive Failure";

            var body = $@"
        <html>
        <body>
            <h2>Archive Process Failure Alert</h2>
            <p><strong>Failure Reason:</strong> {failureReason}</p>
            <p><strong>Event Information:</strong> {eventInfo}</p>
            <p><strong>Timestamp:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
            {(retryCount > 0 ? $"<p><strong>Retry Attempts:</strong> {retryCount}</p>" : "")}
            {exceptionDetails}
            <p>Please investigate the logs for more detailed information.</p>
        </body>
        </html>";

            await emailHelper.SendEmailAsync(subject, body, "support@redmist.racing", "noreply@redmist.racing");
            logger.LogInformation("Archive failure email sent successfully for: {failureReason}", failureReason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send archive failure email for: {failureReason}", failureReason);
        }
    }
}
