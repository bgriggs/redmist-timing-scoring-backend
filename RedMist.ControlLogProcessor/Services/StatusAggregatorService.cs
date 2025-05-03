using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.ControlLogProcessor.Services;

public class StatusAggregatorService : BackgroundService
{
    private readonly ILoggerFactory loggerFactory;
    private readonly IConnectionMultiplexer cacheMux;
    private readonly IConfiguration configuration;
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IControlLogFactory controlLogFactory;
    private readonly IHubContext<StatusHub> hubContext;

    private ILogger Logger { get; }
    private ControlLogCache? controlLogCache;
    private int? eventId;


    public StatusAggregatorService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux, IConfiguration configuration, IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory, IHubContext<StatusHub> hubContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.loggerFactory = loggerFactory;
        this.cacheMux = cacheMux;
        this.configuration = configuration;
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
        this.hubContext = hubContext;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("StatusAggregatorService starting...");
        eventId = configuration.GetValue<int>("event_id");
        if (eventId == null || eventId <= 0)
        {
            Logger.LogError("Event ID is not set or invalid. Cannot start StatusAggregatorService.");
            return;
        }
        controlLogCache = new ControlLogCache(eventId.Value, loggerFactory, tsContext, controlLogFactory);

        var cache = cacheMux.GetDatabase();
        var sub = cacheMux.GetSubscriber();
        Logger.LogInformation("StatusAggregatorService started.");

        // Subscribe to control log requests such as when UI details opens for a car
        await sub.SubscribeAsync(new RedisChannel(Backend.Shared.Consts.SEND_CONTROL_LOG, RedisChannel.PatternMode.Literal),
            async (channel, value) => await ProcessControlLogRequest(value.ToString(), stoppingToken),
            CommandFlags.FireAndForget);


        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendControlLogUpdates(stoppingToken);


                Logger.LogInformation("StatusAggregatorService is running. Event ID: {eventId}", eventId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An error occurred in StatusAggregatorService.");
            }
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
        Logger.LogInformation("StatusAggregatorService stopped.");
    }

    #region Control Log

    private async Task ProcessControlLogRequest(string cmdJson, CancellationToken stoppingToken = default)
    {
        if (controlLogCache == null)
        {
            Logger.LogWarning("ControlLogCache is not initialized. Cannot process control log request.");
            return;
        }

        var cmd = JsonSerializer.Deserialize<SendControlLogCommand>(cmdJson);
        if (cmd == null)
        {
            Logger.LogWarning("Invalid command received: {cj}", cmdJson);
            return;
        }

        CarControlLogs ccl;
        if (string.IsNullOrEmpty(cmd.CarNumber))
        {
            var entries = await controlLogCache.GetControlEntries();
            ccl = new CarControlLogs { CarNumber = string.Empty, ControlLogEntries = entries };
        }
        else // Specific car
        {
            var entries = await controlLogCache.GetCarControlEntries([cmd.CarNumber.ToLower()]);
            ccl = new CarControlLogs { CarNumber = cmd.CarNumber, ControlLogEntries = [.. entries.SelectMany(s => s.Value)] };
        }
        Logger.LogInformation("Sending control logs for event {e} car {c} to new connection {con}", cmd.EventId, cmd.CarNumber, cmd.ConnectionId);
        await SendAsync(cmd.ConnectionId, string.Empty, ccl, stoppingToken);
        //await mediator.Publish(new ControlLogNotification(cmd.EventId, ccl) { ConnectionDestination = cmd.ConnectionId }, stoppingToken);
    }

    private async Task SendControlLogUpdates(CancellationToken stoppingToken)
    {
        if (controlLogCache == null || eventId == null)
        {
            Logger.LogWarning("ControlLogCache is not initialized. Cannot process control log request.");
            return;
        }

        // Single car update
        var changedCars = await controlLogCache.RequestControlLogChanges(stoppingToken);
        var entries = await controlLogCache.GetCarControlEntries([.. changedCars]);
        foreach (var e in entries)
        {
            if (!string.IsNullOrWhiteSpace(e.Key))
            {
                var ccl = new CarControlLogs { CarNumber = e.Key, ControlLogEntries = e.Value };
                await SendAsync(string.Empty, e.Key, ccl, stoppingToken);
                //var notificaiton = new ControlLogNotification(controlLogs.Key, ccl) { CarNumber = e.Key };
                //_ = mediator.Publish(notificaiton, stoppingToken);
            }
        }

        // Full log update
        var fullLog = await controlLogCache.GetControlEntries();
        var fullCcl = new CarControlLogs { CarNumber = string.Empty, ControlLogEntries = fullLog };
        await SendAsync(eventId.ToString()!, string.Empty, fullCcl, stoppingToken);
        //var fullNotificaiton = new ControlLogNotification(controlLogs.Key, fullCcl);
        //_ = mediator.Publish(fullNotificaiton, stoppingToken);
    }

    #endregion

    public async Task SendAsync(string connectionDestination, string carNumber, CarControlLogs logs, CancellationToken cancellationToken)
    {
        if (eventId == null || eventId <= 0)
        {
            Logger.LogError("Event ID is not set or invalid. Cannot send logs.");
            return;
        }
        try
        {
            if (!string.IsNullOrEmpty(connectionDestination))
            {
                Logger.LogTrace("ControlLogNotification: car {c} client {g}", carNumber, connectionDestination);
                await hubContext.Clients.Client(connectionDestination).SendAsync("ReceiveControlLog", logs, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(carNumber))
            {
                string grpKey = $"{eventId}-{carNumber}";
                Logger.LogTrace("ControlLogNotification: car {c} group {g}", carNumber, grpKey);
                await hubContext.Clients.Group(grpKey).SendAsync("ReceiveControlLog", logs, cancellationToken);
            }
            else
            {
                string grpKey = $"{eventId}-cl";
                Logger.LogTrace("ControlLogNotification: event {c} group {g}", eventId, grpKey);
                await hubContext.Clients.Group(grpKey).SendAsync("ReceiveControlLog", logs, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error sending control log update to clients.");
        }
    }
}
