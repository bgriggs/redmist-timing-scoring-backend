using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;

namespace RedMist.SampleProject;

/// <summary>
/// Examples for Red Mist using C#.
/// </summary>
internal class Program
{
    private const int EVENTID = 3;
    private static IConfiguration configuration = null!;

    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting...");
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddUserSecrets<Program>();

        builder.Services.AddSingleton<StatusClient>();
        builder.Services.AddSingleton<ExternalTelemetryClient>();
        var host = builder.Build();
        configuration = host.Services.GetRequiredService<IConfiguration>();

        var statusClient = host.Services.GetRequiredService<StatusClient>();
        var client = host.Services.GetRequiredService<ExternalTelemetryClient>();

        await ShowMenuAsync(statusClient, client);
    }

    static async Task ShowMenuAsync(StatusClient statusClient, ExternalTelemetryClient externalTelemetryClient)
    {
        while (true)
        {
            Console.WriteLine("\n==== Red Mist Sample Client ====");
            Console.WriteLine("\nStatus API:");
            Console.WriteLine("1. Load Recent Events");
            Console.WriteLine("2. Load Event");
            Console.WriteLine("3. Load Event Status");
            Console.WriteLine("4. Load Car Laps");
            Console.WriteLine("5. Load Sessions");
            Console.WriteLine("6. Load Session Results");
            Console.WriteLine("7. Load Competitor Metadata");
            Console.WriteLine("8. Load Control Log");
            Console.WriteLine("9. Load Car Control Logs");
            Console.WriteLine("10. Load In-Car Driver Mode Payload");
            Console.WriteLine("11. Load Flags");
            Console.WriteLine("\nExternal Telemetry:");
            Console.WriteLine("12. Set Driver External Telemetry");
            Console.WriteLine("13. Remove Driver External Telemetry");
            Console.WriteLine("14. Set Video External Telemetry");
            Console.WriteLine("15. Remove Video External Telemetry");
            Console.WriteLine("\n0. Exit");
            Console.Write("\nSelect an option: ");

            var input = Console.ReadLine();
            if (!int.TryParse(input, out int choice))
            {
                Console.WriteLine("Invalid input. Please enter a number.");
                continue;
            }

            try
            {
                switch (choice)
                {
                    case 0:
                        Console.WriteLine("Exiting...");
                        return;
                    case 1:
                        await LoadRecentEventsAsync(statusClient);
                        break;
                    case 2:
                        await LoadEventAsync(statusClient);
                        break;
                    case 3:
                        await LoadEventStatusAsync(statusClient);
                        break;
                    case 4:
                        await LoadCarLapsAsync(statusClient);
                        break;
                    case 5:
                        await LoadSessionsAsync(statusClient);
                        break;
                    case 6:
                        await LoadSessionResultsAsync(statusClient);
                        break;
                    case 7:
                        await LoadCompetitorMetadataAsync(statusClient);
                        break;
                    case 8:
                        await LoadControlLogAsync(statusClient);
                        break;
                    case 9:
                        await LoadCarControlLogsAsync(statusClient);
                        break;
                    case 10:
                        await LoadInCarDriverModePayloadAsync(statusClient);
                        break;
                    case 11:
                        await LoadFlagsAsync(statusClient);
                        break;
                    case 12:
                        await SetDriverExternalTelemetryAsync(externalTelemetryClient);
                        break;
                    case 13:
                        await RemoveDriverExternalTelemetryAsync(externalTelemetryClient);
                        break;
                    case 14:
                        await SetVideoExternalTelemetryAsync(externalTelemetryClient);
                        break;
                    case 15:
                        await RemoveVideoExternalTelemetryAsync(externalTelemetryClient);
                        break;
                    default:
                        Console.WriteLine("Invalid option. Please try again.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to continue...");
            Console.ReadLine();
        }
    }

    /// <summary>
    /// Sets driver names.
    /// </summary>
    static async Task SetDriverExternalTelemetryAsync(ExternalTelemetryClient client)
    {
        // Either the driver and video can be linked by EventId and CarNumber or by TransponderId.
        var driverCar = new DriverInfo { EventId = EVENTID, CarNumber = "60", DriverId = "driver-001", DriverName = "Jane Doe" };
        var driverTrans = new DriverInfo { TransponderId = 1329228, DriverName = "Some Driver" };
        bool result = await client.UpdateDriversAsync([driverCar, driverTrans]);
        Console.WriteLine($"Set driver result: {result}");
    }

    /// <summary>
    /// Sets driver names.
    /// </summary>
    static async Task RemoveDriverExternalTelemetryAsync(ExternalTelemetryClient client)
    {
        // Either the driver and video can be linked by EventId and CarNumber or by TransponderId.
        var driverCar = new DriverInfo { EventId = EVENTID, CarNumber = "60" };
        var driverTrans = new DriverInfo { TransponderId = 1329228 };
        bool result = await client.UpdateDriversAsync([driverCar, driverTrans]);
        Console.WriteLine($"Remove driver result: {result}");
    }

    /// <summary>
    /// Sends car video details.
    /// </summary>
    static async Task SetVideoExternalTelemetryAsync(ExternalTelemetryClient client)
    {
        // Either the driver and video can be linked by EventId and CarNumber or by TransponderId.
        var videoCar = new VideoMetadata
        {
            EventId = EVENTID,
            CarNumber = "60",
            IsLive = true,
            SystemType = VideoSystemType.Generic,
            Destinations = [new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com" }]
        };

        var videoTrans = new VideoMetadata
        {
            TransponderId = 14451114,
            IsLive = true,
            SystemType = VideoSystemType.Sentinel,
            Destinations = [new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com" }]
        };

        var videoAll = new VideoMetadata
        {
            EventId = EVENTID,
            CarNumber = "72",
            //TransponderId = 1329228,
            IsLive = true,
            SystemType = VideoSystemType.MyRacesLive,
            Destinations = [new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com" }]
        };

        var result = await client.UpdateCarVideosAsync([videoCar, videoTrans, videoAll]);
        Console.WriteLine($"Set car video result: {result}");
    }

        /// <summary>
        /// Removes car video details.
        /// </summary>
        static async Task RemoveVideoExternalTelemetryAsync(ExternalTelemetryClient client)
        {
            var videoCar = new VideoMetadata { EventId = EVENTID, CarNumber = "60" };

            var videoTrans = new VideoMetadata { TransponderId = 14451114 };

            var videoAll = new VideoMetadata { EventId = EVENTID, CarNumber = "72", TransponderId = 1329228 };
            var result = await client.UpdateCarVideosAsync([videoCar, videoTrans, videoAll]);
            Console.WriteLine($"Remove car video result: {result}");
        }

        /// <summary>
        /// Loads recent events.
        /// </summary>
        static async Task LoadRecentEventsAsync(StatusClient client)
        {
            Console.WriteLine("Loading recent events...");
            var events = await client.LoadRecentEventsAsync();
            Console.WriteLine($"Found {events.Count} events:");
            foreach (var evt in events)
            {
                Console.WriteLine($"  {JsonSerializer.Serialize(evt)}");
            }
        }

        /// <summary>
        /// Loads a specific event.
        /// </summary>
        static async Task LoadEventAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine($"Loading event {eventId}...");
                var evt = await client.LoadEventAsync(eventId);
                if (evt != null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(evt, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.WriteLine("Event not found.");
                }
            }
            else
            {
                Console.WriteLine("Invalid Event ID.");
            }
        }

        /// <summary>
        /// Loads current event status.
        /// </summary>
        static async Task LoadEventStatusAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine($"Loading event status for {eventId}...");
                var status = await client.LoadEventStatusAsync(eventId);
                if (status != null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.WriteLine("Status not found.");
                }
            }
            else
            {
                Console.WriteLine("Invalid Event ID.");
            }
        }

        /// <summary>
        /// Loads car laps.
        /// </summary>
        static async Task LoadCarLapsAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (!int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine("Invalid Event ID.");
                return;
            }

            Console.Write("Enter Session ID: ");
            if (!int.TryParse(Console.ReadLine(), out int sessionId))
            {
                Console.WriteLine("Invalid Session ID.");
                return;
            }

            Console.Write("Enter Car Number: ");
            var carNumber = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(carNumber))
            {
                Console.WriteLine("Invalid Car Number.");
                return;
            }

            Console.WriteLine($"Loading laps for car {carNumber}...");
            var laps = await client.LoadCarLapsAsync(eventId, sessionId, carNumber);
            Console.WriteLine($"Found {laps.Count} laps:");
            foreach (var lap in laps.Take(10))
            {
                Console.WriteLine($"  {JsonSerializer.Serialize(lap)}");
            }
            if (laps.Count > 10)
            {
                Console.WriteLine($"  ... and {laps.Count - 10} more laps");
            }
        }

        /// <summary>
        /// Loads sessions for an event.
        /// </summary>
        static async Task LoadSessionsAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine($"Loading sessions for event {eventId}...");
                var sessions = await client.LoadSessionsAsync(eventId);
                Console.WriteLine($"Found {sessions.Count} sessions:");
                foreach (var session in sessions)
                {
                    Console.WriteLine($"  {JsonSerializer.Serialize(session)}");
                }
            }
            else
            {
                Console.WriteLine("Invalid Event ID.");
            }
        }

        /// <summary>
        /// Loads session results.
        /// </summary>
        static async Task LoadSessionResultsAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (!int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine("Invalid Event ID.");
                return;
            }

            Console.Write("Enter Session ID: ");
            if (!int.TryParse(Console.ReadLine(), out int sessionId))
            {
                Console.WriteLine("Invalid Session ID.");
                return;
            }

            Console.WriteLine($"Loading results for session {sessionId}...");
            var results = await client.LoadSessionResultsAsync(eventId, sessionId);
            if (results != null)
            {
                Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine("Results not found.");
            }
        }

        /// <summary>
        /// Loads competitor metadata.
        /// </summary>
        static async Task LoadCompetitorMetadataAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (!int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine("Invalid Event ID.");
                return;
            }

            Console.Write("Enter Car Number: ");
            var carNumber = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(carNumber))
            {
                Console.WriteLine("Invalid Car Number.");
                return;
            }

            Console.WriteLine($"Loading metadata for car {carNumber}...");
            var metadata = await client.LoadCompetitorMetadataAsync(eventId, carNumber);
            if (metadata != null)
            {
                Console.WriteLine(JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine("Metadata not found.");
            }
        }

        /// <summary>
        /// Loads control log.
        /// </summary>
        static async Task LoadControlLogAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine($"Loading control log for event {eventId}...");
                var entries = await client.LoadControlLogAsync(eventId);
                Console.WriteLine($"Found {entries.Count} control log entries:");
                foreach (var entry in entries.Take(10))
                {
                    Console.WriteLine($"  {JsonSerializer.Serialize(entry)}");
                }
                if (entries.Count > 10)
                {
                    Console.WriteLine($"  ... and {entries.Count - 10} more entries");
                }
            }
            else
            {
                Console.WriteLine("Invalid Event ID.");
            }
        }

        /// <summary>
        /// Loads car control logs.
        /// </summary>
        static async Task LoadCarControlLogsAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (!int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine("Invalid Event ID.");
                return;
            }

            Console.Write("Enter Car Number: ");
            var carNumber = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(carNumber))
            {
                Console.WriteLine("Invalid Car Number.");
                return;
            }

            Console.WriteLine($"Loading control logs for car {carNumber}...");
            var logs = await client.LoadCarControlLogsAsync(eventId, carNumber);
            if (logs != null)
            {
                Console.WriteLine(JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine("Control logs not found.");
            }
        }

        /// <summary>
        /// Loads in-car driver mode payload.
        /// </summary>
        static async Task LoadInCarDriverModePayloadAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (!int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine("Invalid Event ID.");
                return;
            }

            Console.Write("Enter Car Number: ");
            var carNumber = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(carNumber))
            {
                Console.WriteLine("Invalid Car Number.");
                return;
            }

            Console.WriteLine($"Loading in-car payload for car {carNumber}...");
            var payload = await client.LoadInCarDriverModePayloadAsync(eventId, carNumber);
            if (payload != null)
            {
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine("Payload not found.");
            }
        }

        /// <summary>
        /// Loads flags.
        /// </summary>
        static async Task LoadFlagsAsync(StatusClient client)
        {
            Console.Write("Enter Event ID: ");
            if (!int.TryParse(Console.ReadLine(), out int eventId))
            {
                Console.WriteLine("Invalid Event ID.");
                return;
            }

            Console.Write("Enter Session ID: ");
            if (!int.TryParse(Console.ReadLine(), out int sessionId))
            {
                Console.WriteLine("Invalid Session ID.");
                return;
            }

            Console.WriteLine($"Loading flags for session {sessionId}...");
            var flags = await client.LoadFlagsAsync(eventId, sessionId);
            Console.WriteLine($"Found {flags.Count} flag periods:");
            foreach (var flag in flags)
            {
                Console.WriteLine($"  {JsonSerializer.Serialize(flag)}");
            }
        }
    }
