using RedMist.Backend.Shared;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.Video;

/// <summary>
/// Provides functionality to enrich car position data with video metadata and status updates during a session.
/// </summary>
/// <remarks>The VideoEnricher class processes incoming timing messages and applies cached video metadata to car
/// positions. It is used to synchronize video system status with live car data in a session context.
/// Instances require a session context, a logger factory, and a cache multiplexer for operation. This class is not
/// thread-safe; concurrent access should be managed externally if required.</remarks>
public class VideoEnricher
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionContext;
    private readonly IConnectionMultiplexer cacheMux;


    public VideoEnricher(SessionContext context, ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux)
    {
        sessionContext = context;
        this.cacheMux = cacheMux;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    /// <summary>
    /// Handles an incoming timing message to update car position video status.
    /// </summary>
    public PatchUpdates? Process(TimingMessage message)
    {
        if (string.IsNullOrEmpty(message.Data))
        {
            Logger.LogWarning("Unable to deserialize VideoMetadata from message data: message data is null or empty");
            return null;
        }

        VideoMetadata? video;
        try
        {
            video = JsonSerializer.Deserialize<VideoMetadata>(message.Data);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Unable to deserialize VideoMetadata from message data: {Data}", message.Data);
            return null;
        }
        
        if (video == null)
        {
            Logger.LogWarning("Unable to deserialize VideoMetadata from message data: {Data}", message.Data);
            return null;
        }

        CarPositionPatch? patch = null;
        if (!string.IsNullOrWhiteSpace(video.CarNumber))
        {
            if (sessionContext.EventId != video.EventId)
            {
                Logger.LogTrace("VideoMetadata event ID {e} is not this event, ignoring.", video.EventId);
                return null;
            }

            var car = sessionContext.GetCarByNumber(video.CarNumber);
            if (car != null)
            {
                patch = UpdateCar(video, car);
            }
        }
        else if (video.TransponderId > 0)
        {
            var number = sessionContext.GetCarNumberForTransponder(video.TransponderId);
            if (number != null)
            {
                var car = sessionContext.GetCarByNumber(number);
                if (car != null)
                {
                    patch = UpdateCar(video, car);
                }
            }
        }
        else
        {
            Logger.LogTrace("Unable to resolve car for VideoMetadata event:{e}, car:{c}, transponder:{t}", 
                video.EventId, video.CarNumber, video.TransponderId);
        }

        if (patch != null)
        {
            return new PatchUpdates([], [patch]);
        }
        return null;
    }

    /// <summary>
    /// Gets all current entries from cache to apply current status and clear out 
    /// expired entries. This should be called periodically to ensure car positions
    /// are up to date, such as every 60 seconds.
    /// </summary>
    public async Task<PatchUpdates?> ProcessApplyFullAsync()
    {
        var patches = new List<CarPositionPatch>();
        var cache = cacheMux.GetDatabase();
        var cars = sessionContext.SessionState.CarPositions.ToArray();
        foreach (var car in cars)
        {
            if (!string.IsNullOrEmpty(car.Number))
            {
                var patch = await ProcessCarAsync(car.Number, cache);
                if (patch != null)
                {
                    patches.Add(patch);
                }
            }
        }

        if (patches.Count > 0)
        {
            return new PatchUpdates([], [.. patches]);
        }
        return null;
    }

    public async Task<CarPositionPatch?> ProcessCarAsync(string carNumber, IDatabase? cache = null)
    {
        if (string.IsNullOrEmpty(carNumber))
        {
            Logger.LogWarning("Car number is null or empty in ProcessCarAsync");
            return null;
        }

        VideoMetadata? videoMetadata = null;
        var car = sessionContext.GetCarByNumber(carNumber);
        if (car == null)
        {
            Logger.LogWarning("Car not found for number {CarNumber} in ProcessCarAsync", carNumber);
            return null;
        }
        CarPositionPatch? patch = null;
        cache ??= cacheMux.GetDatabase();

        // Event and Car Number
        var key1 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, car.Number, 0);
        var json = await cache.StringGetAsync(key1);
        if (json.HasValue)
        {
            try
            {
                videoMetadata = JsonSerializer.Deserialize<VideoMetadata>(json!.ToString());
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Unable to deserialize VideoMetadata from cache for car {CarNumber}, key {Key}", car.Number, key1);
            }
        }
        else if (car.TransponderId > 0)
        {
            // Transponder only
            var key2 = string.Format(Consts.EVENT_VIDEO_KEY, 0, string.Empty, car.TransponderId);
            json = await cache.StringGetAsync(key2);
            if (json.HasValue)
            {
                try
                {
                    videoMetadata = JsonSerializer.Deserialize<VideoMetadata>(json!.ToString());
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "Unable to deserialize VideoMetadata from cache for car {CarNumber}, key {Key}", car.Number, key2);
                }
            }
            else // Event, Car Number, and Transponder
            {
                var key3 = string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, car.Number, car.TransponderId);
                json = await cache.StringGetAsync(key3);
                if (json.HasValue)
                {
                    try
                    {
                        videoMetadata = JsonSerializer.Deserialize<VideoMetadata>(json!.ToString());
                    }
                    catch (JsonException ex)
                    {
                        Logger.LogWarning(ex, "Unable to deserialize VideoMetadata from cache for car {CarNumber}, key {Key}", car.Number, key3);
                    }
                }
            }
        }

        if (videoMetadata != null)
        {
            patch = UpdateCar(videoMetadata, car);
        }
        else
        {
            // No video metadata found, clear out any existing InCarVideo status
            if (car.InCarVideo != null)
            {
                car.InCarVideo = null;
                // Send "empty" status since null will be ignored
                patch = new CarPositionPatch()
                {
                    Number = car.Number,
                    InCarVideo = new VideoStatus
                    {
                        VideoSystemType = VideoSystemType.None,
                        VideoDestination = new VideoDestination(),
                    }
                };
            }
        }

        return patch;
    }

    private static CarPositionPatch UpdateCar(VideoMetadata video, CarPosition car)
    {
        var status = new VideoStatus
        {
            VideoSystemType = video.SystemType,
            VideoDestination = video.Destinations?.FirstOrDefault() ?? new VideoDestination(),
        };

        car.InCarVideo = status;
        return new CarPositionPatch()
        {
            Number = car.Number,
            InCarVideo = status,
        };
    }
}
