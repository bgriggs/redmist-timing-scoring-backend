using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedMist.SampleProject;
using RedMist.TimingCommon.Models;

namespace RedMist.TeamSample;

internal class Program
{
    const string MyCarNumber = "2";
    const string MyClass = "GTO";
    private static ILogger logger = null!;
    private static StatusClient statusClient = null!;
    private static int? eventId = null;
    private static SessionState? lastSessionState = null;

    /// <summary>
    /// Team or car example focusing on a car's status and immediate competition.
    /// </summary>
    static async Task Main(string[] args)
    {
        // Build configuration
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddUserSecrets<Program>();

        // Configure logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton<StatusClient>();
        builder.Services.AddSingleton<StatusSubscriptionClient>();
        var host = builder.Build();

        logger = host.Services.GetRequiredService<ILogger<Program>>();
        statusClient = host.Services.GetRequiredService<StatusClient>();
        var subscriptionClient = host.Services.GetRequiredService<StatusSubscriptionClient>();

        // Need to load the event list to get the actual event IDs
        var events = await statusClient.LoadRecentEventsAsync();
        foreach (var ev in events)
        {
            logger.LogInformation($"Event ID: {ev.Id}, Name: {ev.EventName}, Track: {ev.TrackName}, Live: {ev.IsLive}");
        }

        // Use your event ID here. This is just an example to get a live event.
        eventId = events.FirstOrDefault(e => e.IsLive)?.Id;
        if (eventId == null)
        {
            logger.LogError("No live event found");
            return;
        }

        // Get the initial full event data
        lastSessionState = await statusClient.LoadEventStatusAsync(eventId.Value);
        if (lastSessionState == null)
        {
            logger.LogError("Failed to load event status");
            return;
        }
        logger.LogInformation($"Loaded event status for event ID {eventId.Value}, current session: {lastSessionState.SessionName}");

        // Subscribe to car position patches for the event
        subscriptionClient.CarPatchesReceived += SubscriptionClient_CarPatchesReceived;
        subscriptionClient.ConnectionStatusChanged += SubscriptionClient_ConnectionStatusChanged;
        await subscriptionClient.SubscribeToEventAsync(eventId.Value);

        logger.LogInformation("Application started");
        Console.ReadLine();
    }

    private static void SubscriptionClient_CarPatchesReceived(CarPositionPatch[]? carUpdates)
    {
        if (carUpdates == null || carUpdates.Length == 0)
            return;

        foreach (var car in carUpdates)
        {
            // Display information about your car each lap, the overall leader, and class leader
            if (car.Number == MyCarNumber)
            {
                logger.LogInformation($"Car {car.Number} is in position {car.OverallPosition} last lap {car.LastLapTime}.");
                logger.LogInformation($"Car {car.Number} pit status is: {car.IsInPit}");
                logger.LogInformation($"Car gap to overall leader is {car.OverallGap} and in class {car.InClassGap}");
            }
            // Display information when about hte overall leader each lap
            else if (car.OverallPosition == 1 && car.Number != MyCarNumber)
            {
                logger.LogInformation($"Car {car.Number} is in the lead.");
                logger.LogInformation($"Car {car.Number} pit status is: {car.IsInPit}");
            }
            // Display information about the class leader each lap
            else if (car.ClassPosition == 1 && car.Class == MyClass && car.Number != MyCarNumber)
            {
                logger.LogInformation($"Car {car.Number} is leading class {MyClass}.");
                logger.LogInformation($"Car {car.Number} pit status is: {car.IsInPit}");
            }
        }
    }

    private static async void SubscriptionClient_ConnectionStatusChanged(Microsoft.AspNetCore.SignalR.Client.HubConnectionState s)
    {
        if (s == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            logger.LogInformation("Connected to subscription service");

            // If you lose connection and need to load all laps for your car, use:
            if (lastSessionState != null)
            {
                var carLaps = await statusClient.LoadCarLapsAsync(lastSessionState.EventId, lastSessionState.SessionId, MyCarNumber);
                logger.LogInformation($"Loaded {carLaps.Count} laps for car number {MyCarNumber}");
            }
        }
        else
        {
            logger.LogWarning($"Subscription service connection status changed: {s}");
        }
    }

    
}
