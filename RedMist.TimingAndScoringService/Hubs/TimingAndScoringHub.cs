using Microsoft.AspNetCore.SignalR;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;

namespace RedMist.TimingAndScoringService.Hubs;

public class TimingAndScoringHub : Hub
{
    private readonly RMDataProcessor rmDataProcessor;

    private ILogger Logger { get; }

    public TimingAndScoringHub(ILoggerFactory loggerFactory, RMDataProcessor rmDataProcessor)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.rmDataProcessor = rmDataProcessor;
    }

    public async override Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        Logger.LogInformation("Client connected: {0}", Context.ConnectionId);
    }

    public async override Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
        Logger.LogInformation("Client disconnected: {0}", Context.ConnectionId);
    }

    /// <summary>
    /// Receives a sent message from a RMonitor relay.
    /// </summary>
    /// <param name="command">RMonitor command string</param>
    /// <returns></returns>
    /// <see cref="https://github.com/bradfier/rmonitor/blob/master/docs/RMonitor%20Timing%20Protocol.pdf"/>
    public Task SendRMonitor(string command)
    {
        Logger.LogDebug("RX-RM: {0}", command);
        rmDataProcessor.ProcessUpdate(command);
        return Task.CompletedTask;
    }
}
