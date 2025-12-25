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

        builder.Services.AddSingleton<ExternalTelemetryClient>();
        var host = builder.Build();
        configuration = host.Services.GetRequiredService<IConfiguration>();

        var client = host.Services.GetRequiredService<ExternalTelemetryClient>();

        //Console.WriteLine("Press any key to set drivers");
        //Console.ReadLine();
        //await SetDriverExternalTelemetryAsync(client);

        //Console.WriteLine("Press any key to remove drivers");
        //Console.ReadLine();
        //await RemoveDriverExternalTelemetryAsync(client);

        Console.WriteLine("Press any key to set video entries");
        Console.ReadLine();
        await SetVideoExternalTelemetryAsync(client);

        Console.WriteLine("Press any key to remove video entries");
        Console.ReadLine();
        await RemoveVideoExternalTelemetryAsync(client);
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
}
