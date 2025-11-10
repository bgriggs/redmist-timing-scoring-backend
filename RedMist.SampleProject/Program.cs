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

        await SetExternalTelemetryAsync(host.Services.GetRequiredService<ExternalTelemetryClient>());
    }

    /// <summary>
    /// Sets up external telemetry such as car video and driver.
    /// </summary>
    /// <returns></returns>
    static async Task SetExternalTelemetryAsync(ExternalTelemetryClient client)
    {
        // Either the driver and video can be linked by EventId and CarNumber or by TransponderId.
        var driverCar = new DriverInfo { EventId = 1234, CarNumber = "42", DriverId = "driver-001", DriverName = "Jane Doe" };
        var driverTrans = new DriverInfo { TransponderId = 1, DriverId = "driver-001", DriverName = "Jane Doe" };
        bool result = await client.UpdateDriversAsync([driverCar, driverTrans]);
        Console.WriteLine($"UpdateDriversAsync result: {result}");

        var videoCar = new VideoMetadata
        {
            EventId = 1,
            CarNumber = "123",
            IsLive = true,
            SystemType = VideoSystemType.Generic,
            Destinations =
            [
                new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com" },
            ]
        };
        var videoTrans = new VideoMetadata
        {
            TransponderId = 123456,
            IsLive = true,
            SystemType = VideoSystemType.Sentinel,
            Destinations = [new VideoDestination { Type = VideoDestinationType.Youtube, Url = "https://youtube.com" }]
        };
        result = await client.UpdateCarVideosAsync([videoCar, videoTrans]);
        Console.WriteLine($"UpdateCarVideoAsync result: {result}");
    }
}
