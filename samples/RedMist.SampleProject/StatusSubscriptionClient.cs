using System.Text.Json;
using BigMission.Shared.Auth;
using BigMission.Shared.SignalR;
using BigMission.Shared.Utilities;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarDriverMode;

namespace RedMist.SampleProject;

/// <summary>
/// Subscription based client for event status updates.
/// </summary>
internal class StatusSubscriptionClient : HubClientBase
{
    private HubConnection? hub;
    private ILogger Logger { get; }
    private readonly IConfiguration configuration;
    private readonly Debouncer debouncer = new(TimeSpan.FromMilliseconds(5));
    private int? subscribedEventId;
    private (int eventId, string car)? subscribedInCarDriverEventIdAndCar;
    public event Action<CarPositionPatch[]?>? CarPatchesReceived;


    public StatusSubscriptionClient(ILoggerFactory loggerFactory, IConfiguration configuration) : base(loggerFactory, configuration)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        ConnectionStatusChanged += HubClient_ConnectionStatusChanged;
        this.configuration = configuration;
    }


    protected override HubConnection GetConnection()
    {
        string hubUrl = configuration["Hub:Url"] ?? throw new InvalidOperationException("Hub URL is not configured.");
        string authUrl = configuration["Keycloak:AuthServerUrl"] ?? throw new InvalidOperationException("Keycloak URL is not configured.");
        string realm = configuration["Keycloak:Realm"] ?? throw new InvalidOperationException("Keycloak realm is not configured.");

        var builder = new HubConnectionBuilder().WithUrl(hubUrl, delegate (HttpConnectionOptions options)
        {
            options.AccessTokenProvider = async delegate
            {
                try
                {
                    var clientId = GetClientId();
                    var clientSecret = GetClientSecret();
                    return await KeycloakServiceToken.RequestClientToken(authUrl, realm, clientId, clientSecret);
                }
                catch (Exception exception)
                {
                    Logger.LogError(exception, "Failed to get server hub access token");
                    return null;
                }
            };
        })
        .WithAutomaticReconnect(new InfiniteRetryPolicy())
        .TryAddMessagePack();

        var hubConnection = builder.Build();

        InitializeStateLogging(hubConnection);
        return hubConnection;
    }

    private void HubClient_ConnectionStatusChanged(HubConnectionState obj)
    {
        if (hub == null)
            return;
        try
        {
            if (hub.State == HubConnectionState.Connected)
            {
                if (subscribedEventId != null)
                {
                    _ = debouncer.ExecuteAsync(async () =>
                    {
                        try
                        {
                            Logger.LogInformation("Invoking SubscribeToEventV2 for event {EventId}", subscribedEventId);
                            await hub.InvokeAsync("SubscribeToEventV2", subscribedEventId.Value);
                            Logger.LogInformation("Successfully invoked SubscribeToEventV2 for event {EventId}", subscribedEventId);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Failed to invoke SubscribeToEventV2 for event {EventId}", subscribedEventId);
                            throw;
                        }
                    });
                }
                else if (subscribedInCarDriverEventIdAndCar != null)
                {
                    _ = debouncer.ExecuteAsync(async () =>
                    {
                        try
                        {
                            Logger.LogInformation("Invoking SubscribeToInCarDriverEventV2 for event {EventId}, car {Car}",
                                subscribedInCarDriverEventIdAndCar.Value.eventId, subscribedInCarDriverEventIdAndCar.Value.car);
                            await hub.InvokeAsync("SubscribeToInCarDriverEventV2",
                                subscribedInCarDriverEventIdAndCar.Value.eventId,
                                subscribedInCarDriverEventIdAndCar.Value.car);
                            Logger.LogInformation("Successfully invoked SubscribeToInCarDriverEventV2");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Failed to invoke SubscribeToInCarDriverEventV2");
                            throw;
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to resubscribe to event");
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }

    #region Car Timing Status

    public async Task SubscribeToEventAsync(int eventId)
    {
        if (hub != null)
        {
            try
            {
                await hub.DisposeAsync();
                hub = null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to dispose hub connection");
            }
        }

        try
        {
            subscribedEventId = eventId;
            hub = StartConnection();

            hub.Remove("ReceiveSessionPatch");
            hub.On("ReceiveSessionPatch", (SessionStatePatch ssp) => ProcessSessionMessage(ssp));

            hub.Remove("ReceiveCarPatches");
            hub.On("ReceiveCarPatches", (CarPositionPatch[] cpps) => ProcessCarPatches(cpps));

            hub.Remove("ReceiveReset");
            hub.On("ReceiveReset", ProcessReset);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to subscribe to event");
        }
    }

    public async Task UnsubscribeFromEventAsync(int eventId)
    {
        subscribedEventId = null;

        if (hub == null)
            return;

        try
        {
            await hub.InvokeAsync("UnsubscribeFromEventV2", eventId);
            await hub.DisposeAsync();
            hub = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to dispose hub connection");
        }
    }

    private void ProcessSessionMessage(SessionStatePatch sessionStatePatch)
    {
        try
        {
            Logger.LogInformation("RX Session Patch: {SessionPatch}", JsonSerializer.Serialize(sessionStatePatch));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process session message.");
        }
    }

    private void ProcessCarPatches(CarPositionPatch[] carPatches)
    {
        try
        {
            Logger.LogInformation("RX Car Patches: {Count}, Data: {CarPatches}", carPatches.Length, JsonSerializer.Serialize(carPatches));
            CarPatchesReceived?.Invoke(carPatches);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process car patches.");
        }
    }

    private void ProcessReset()
    {
        try
        {
            Logger.LogInformation("RX Reset");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process reset message.");
        }
    }

    #endregion

    #region Control Logs

    public async Task SubscribeToControlLogsAsync(int eventId)
    {
        if (hub == null)
            return;

        try
        {
            await hub.InvokeAsync("SubscribeToControlLogs", eventId);

            hub.Remove("ReceiveControlLog");
            hub.On("ReceiveControlLog", (CarControlLogs s) => ProcessControlLogs(s));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to subscribe to control logs");
        }
    }

    public async Task UnsubscribeFromControlLogsAsync(int eventId)
    {
        if (hub == null)
            return;

        try
        {
            await hub.InvokeAsync("UnsubscribeFromControlLogs", eventId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to unsubscribe from control logs");
        }
    }

    public async Task SubscribeToCarControlLogsAsync(int eventId, string carNum)
    {
        if (hub == null)
            return;

        try
        {
            await hub.InvokeAsync("SubscribeToCarControlLogs", eventId, carNum);

            hub.Remove("ReceiveControlLog");
            hub.On("ReceiveControlLog", (CarControlLogs s) => ProcessControlLogs(s));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to subscribe to car control logs");
        }
    }

    public async Task UnsubscribeFromCarControlLogsAsync(int eventId, string carNum)
    {
        if (hub == null)
            return;

        try
        {
            await hub.InvokeAsync("UnsubscribeFromCarControlLogs", eventId, carNum);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to unsubscribe from car control logs");
        }
    }

    private void ProcessControlLogs(CarControlLogs ccl)
    {
        try
        {
            Logger.LogInformation("RX Control Logs: {Count} car {CarNumber}, Data: {ControlLogs}", 
                ccl.ControlLogEntries.Count, ccl.CarNumber, JsonSerializer.Serialize(ccl));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process control log message");
        }
    }

    #endregion

    #region Driver Mode

    public async Task SubscribeToInCarDriverEventAsync(int eventId, string car)
    {
        if (hub != null)
        {
            try
            {
                await hub.DisposeAsync();
                hub = null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to dispose hub connection");
            }
        }

        try
        {
            subscribedInCarDriverEventIdAndCar = (eventId, car);
            hub = StartConnection();

            hub.Remove("ReceiveInCarUpdateV2");
            hub.On("ReceiveInCarUpdateV2", (InCarPayload s) => ProcessInCarPayload(s));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to subscribe to in-car driver event");
        }
    }

    public async Task UnsubscribeFromInCarDriverEventAsync(int eventId, string car)
    {
        subscribedInCarDriverEventIdAndCar = null;

        if (hub == null)
            return;

        try
        {
            await hub.InvokeAsync("UnsubscribeFromInCarDriverEventV2", eventId, car);
            await hub.DisposeAsync();
            hub = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to dispose hub connection");
        }
    }

    private void ProcessInCarPayload(InCarPayload payload)
    {
        try
        {
            if (payload == null)
                return;
            Logger.LogInformation("RX InCarPayload: {Count}, Data: {Payload}", 
                payload.Cars.Count, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process in-car payload");
        }
    }

    #endregion
}
