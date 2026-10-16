using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;

namespace RedMist.EventProcessor.EventStatus.PipelineBlocks;

public class ResetProcessor
{
    private readonly SessionContext sessionContext;
    private readonly IHubContext<StatusHub> hubContext;
    private ILogger Logger { get; }


    public ResetProcessor(SessionContext sessionContext, IHubContext<StatusHub> hubContext, ILoggerFactory loggerFactory)
    {
        this.sessionContext = sessionContext;
        this.hubContext = hubContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }

    public async Task Process()
    {
        try
        {
            Logger.LogInformation("*** Processing RESET for event {EventId} ***", sessionContext.EventId);
            sessionContext.ResetCommand();
            await SendClientResetAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing reset");
        }
    }

    /// <summary>
    /// Tells connected clients to drop their field and re-read the status, without clearing the session.
    /// For changes the patch feed cannot express, such as a car leaving the field.
    /// </summary>
    public virtual async Task SendClientResetAsync()
    {
        var subKey = string.Format(Consts.EVENT_SUB_V2, sessionContext.EventId.ToString());
        await hubContext.Clients.Group(subKey).SendAsync("ReceiveReset", sessionContext.CancellationToken);
    }
}
