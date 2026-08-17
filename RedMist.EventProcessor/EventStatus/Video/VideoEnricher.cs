using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Utilities;
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
        var carNumbers = sessionContext.SessionState.CarPositions
            .Select(c => c.Number)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToArray();

        var patches = await ProcessCarsAsync(carNumbers!);
        if (patches.Count > 0)
        {
            return new PatchUpdates([], [.. patches]);
        }
        return null;
    }

    public async Task<CarPositionPatch?> ProcessCarAsync(string carNumber, IDatabase? cache = null)
    {
        var patches = await ProcessCarsAsync([carNumber], cache);
        return patches.Count > 0 ? patches[0] : null;
    }

    /// <summary>
    /// Resolves and applies video metadata for a set of cars.
    ///
    /// The cache reads are issued a tier at a time rather than a car at a time. This runs inside
    /// the pipeline's write lock on every message that touches cars, so a round trip per car put
    /// the whole field's worth of cache latency on the critical path - and video looks in three
    /// places for a car before giving up. Batching each tier costs at most three round trips
    /// however many cars are in the set, and only the cars that fell through the previous tier
    /// take part in the next.
    /// </summary>
    public async Task<List<CarPositionPatch>> ProcessCarsAsync(IEnumerable<string> carNumbers, IDatabase? cache = null)
    {
        var cars = new List<CarPosition>();
        foreach (var carNumber in carNumbers)
        {
            if (string.IsNullOrEmpty(carNumber))
            {
                Logger.LogWarning("Car number is null or empty in ProcessCarAsync");
                continue;
            }

            var car = sessionContext.GetCarByNumber(carNumber);
            if (car == null)
            {
                Logger.LogWarning("Car not found for number {CarNumber} in ProcessCarAsync", carNumber);
                continue;
            }
            cars.Add(car);
        }

        if (cars.Count == 0)
            return [];

        cache ??= cacheMux.GetDatabase();
        var videos = new VideoMetadata?[cars.Count];

        // Event and Car Number
        var remaining = await ReadTierAsync(cache, videos,
            [.. Enumerable.Range(0, cars.Count)],
            i => string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, cars[i].Number, 0),
            cars);

        // Transponder only
        remaining = remaining.Where(i => cars[i].TransponderId > 0).ToList();
        remaining = await ReadTierAsync(cache, videos, remaining,
            i => string.Format(Consts.EVENT_VIDEO_KEY, 0, string.Empty, cars[i].TransponderId),
            cars);

        // Event, Car Number, and Transponder
        await ReadTierAsync(cache, videos, remaining,
            i => string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, cars[i].Number, cars[i].TransponderId),
            cars);

        var patches = new List<CarPositionPatch>();
        for (var i = 0; i < cars.Count; i++)
        {
            var patch = ApplyVideo(videos[i], cars[i]);
            if (patch != null)
                patches.Add(patch);
        }
        return patches;
    }

    /// <summary>
    /// Reads one tier of keys for the cars still without metadata and records what it found.
    /// </summary>
    /// <returns>
    /// The cars whose key was absent, which are the ones the next tier is asked about. A key that
    /// is present but unreadable is not among them: that car stops here without metadata, as it
    /// always has.
    /// </returns>
    private async Task<List<int>> ReadTierAsync(IDatabase cache, VideoMetadata?[] videos,
        List<int> candidates, Func<int, string> keyFor, List<CarPosition> cars)
    {
        if (candidates.Count == 0)
            return [];

        var keys = candidates.Select(keyFor).ToArray();
        var values = await cache.StringGetAllAsync(keys);

        var absent = new List<int>();
        for (var j = 0; j < candidates.Count; j++)
        {
            var i = candidates[j];
            if (!values[j].HasValue)
            {
                absent.Add(i);
                continue;
            }

            try
            {
                videos[i] = JsonSerializer.Deserialize<VideoMetadata>(values[j]!.ToString());
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Unable to deserialize VideoMetadata from cache for car {CarNumber}, key {Key}",
                    cars[i].Number, keys[j]);
            }
        }
        return absent;
    }

    private static CarPositionPatch? ApplyVideo(VideoMetadata? videoMetadata, CarPosition car)
    {
        if (videoMetadata != null)
        {
            return UpdateCar(videoMetadata, car);
        }

        // No video metadata found, clear out any existing InCarVideo status
        if (car.InCarVideo == null)
            return null;

        car.InCarVideo = null;
        // Send "empty" status since null will be ignored
        return new CarPositionPatch()
        {
            Number = car.Number,
            InCarVideo = new VideoStatus
            {
                VideoSystemType = VideoSystemType.None,
                VideoDestination = new VideoDestination(),
            }
        };
    }

    private static CarPositionPatch UpdateCar(VideoMetadata video, CarPosition car)
    {
        VideoDestination destination;
        if (video.Destinations.Any(d => d.Type == VideoDestinationType.DirectSrt))
        {
            // Prefer SRT to avoid YouTube copyright issues
            destination = video.Destinations.First(d => d.Type == VideoDestinationType.DirectSrt);
        }
        else
        {
            destination = video.Destinations.FirstOrDefault() ?? new VideoDestination();
        }

        var status = new VideoStatus { VideoSystemType = video.SystemType, VideoDestination = destination };

        // Perform direct update to session state car
        car.InCarVideo = status;

        // Capture patch to send out to clients
        return new CarPositionPatch() { Number = car.Number, InCarVideo = status };
    }
}
