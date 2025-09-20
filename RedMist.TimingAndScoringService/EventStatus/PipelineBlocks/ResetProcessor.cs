using Microsoft.AspNetCore.SignalR;
using RedMist.Backend.Shared.Hubs;

namespace RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;

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
        var eventId = sessionContext.EventId.ToString();
        Logger.LogInformation("*** Processing RESET for event {EventId} ***", eventId);
        sessionContext.ResetCommand();
        await hubContext.Clients.Group(eventId).SendAsync("ReceiveReset", sessionContext.CancellationToken);
    }
}
