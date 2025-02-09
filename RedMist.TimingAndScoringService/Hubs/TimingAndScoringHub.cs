using Microsoft.AspNetCore.SignalR;

namespace RedMist.TimingAndScoringService.Hubs;

public class TimingAndScoringHub : Hub
{
    private ILogger Logger { get; }

    public TimingAndScoringHub(ILoggerFactory loggerFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
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
    public Task Send(string command)
    {
        Logger.LogDebug("RX: {0}", command);
        return Task.CompletedTask;
    }
}
