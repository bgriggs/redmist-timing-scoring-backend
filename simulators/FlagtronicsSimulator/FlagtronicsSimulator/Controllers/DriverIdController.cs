using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace FlagtronicsSimulator.Controllers;

[ApiController]
[Route("api/driverid/{apiKey}")]
public class DriverIdController : ControllerBase
{
    private static readonly string ValidApiKey = "12345678-1234-1234-1234-123456789abc";
    private static long _nextEventId = 1;
    private static readonly ConcurrentQueue<DriverIdEvent> _eventBuffer = new();
    private static readonly List<DriverMapping> _driverMappings = [];
    private static readonly Lock _pollLock = new();
    private static readonly Dictionary<long, TaskCompletionSource<bool>> _pollWaiters = [];

    static DriverIdController()
    {
        // Initialize fake driver mappings
        _driverMappings.Add(new DriverMapping
        {
            DriverId = 70012345,
            RfidTag = "E2003412AB01",
            BleMacAddress = "AA:BB:CC:DD:EE:FF",
            Notes = "Team Alpha driver"
        });
        _driverMappings.Add(new DriverMapping
        {
            DriverId = 70012346,
            RfidTag = "E2003412AB02",
            BleMacAddress = "AA:BB:CC:DD:EE:F0",
            Notes = "Team Beta driver"
        });
        _driverMappings.Add(new DriverMapping
        {
            DriverId = 70012347,
            RfidTag = "E2003412AB03",
            BleMacAddress = "AA:BB:CC:DD:EE:F1",
            Notes = "Team Gamma driver"
        });

        // Add some initial events
        AddFakeEvent(70012345, "John Smith", "60", 1001, "E2003412AB01", "AA:BB:CC:DD:EE:FF");
        AddFakeEvent(70012346, "Jane Doe", "1", 1001, "E2003412AB02", "AA:BB:CC:DD:EE:F0");
        AddFakeEvent(70012347, "Mike Johnson", "908", 1002, "E2003412AB03", "AA:BB:CC:DD:EE:F1");
    }

    private static void AddFakeEvent(long driverId, string driverName, string carNumber, int deviceId, string rfid, string bleMac)
    {
        var evt = new DriverIdEvent
        {
            EventId = _nextEventId++,
            Timestamp = DateTime.UtcNow,
            DriverId = driverId,
            DriverName = driverName,
            CarNumber = carNumber,
            Ft200DeviceId = deviceId,
            Rfid = rfid,
            BleMac = bleMac,
            DeviceLookupFound = true
        };
        _eventBuffer.Enqueue(evt);

        // Keep only last 100 events
        while (_eventBuffer.Count > 100)
        {
            _eventBuffer.TryDequeue(out _);
        }

        // Notify any waiting poll requests
        lock (_pollLock)
        {
            foreach (var waiter in _pollWaiters.Values)
            {
                waiter.TrySetResult(true);
            }
            _pollWaiters.Clear();
        }
    }

    private IActionResult ValidateApiKey(string apiKey)
    {
        if (apiKey != ValidApiKey)
        {
            return Unauthorized(new { error = "Invalid API key" });
        }
        return null!;
    }

    /// <summary>
    /// GET /api/driverid/{apiKey}
    /// Returns the most recent driver identification event
    /// </summary>
    [HttpGet]
    public IActionResult GetLatestEvent(string apiKey)
    {
        var validationResult = ValidateApiKey(apiKey);
        if (validationResult != null) return validationResult;

        var latestEvent = _eventBuffer.Reverse().FirstOrDefault();
        if (latestEvent == null)
        {
            return NoContent();
        }

        return Ok(latestEvent);
    }

    /// <summary>
    /// GET /api/driverid/{apiKey}/history?count=10
    /// Returns recent events, newest first
    /// </summary>
    [HttpGet("history")]
    public IActionResult GetEventHistory(string apiKey, [FromQuery] int count = 10)
    {
        var validationResult = ValidateApiKey(apiKey);
        if (validationResult != null) return validationResult;

        count = Math.Min(count, 100); // Max 100 events
        var events = _eventBuffer.Reverse().Take(count).ToList();

        return Ok(new
        {
            events,
            count = events.Count
        });
    }

    /// <summary>
    /// GET /api/driverid/{apiKey}/poll?lastEventId=42&timeout=30
    /// Waits for new events using long polling
    /// </summary>
    [HttpGet("poll")]
    public async Task<IActionResult> PollForEvents(string apiKey, [FromQuery] long lastEventId = 0, [FromQuery] int timeout = 30)
    {
        var validationResult = ValidateApiKey(apiKey);
        if (validationResult != null) return validationResult;

        timeout = Math.Min(timeout, 30); // Max 30 seconds

        // Check for existing events
        var newEvents = _eventBuffer.Where(e => e.EventId > lastEventId).ToList();
        if (newEvents.Any())
        {
            return Ok(new
            {
                events = newEvents,
                count = newEvents.Count,
                lastEventId = newEvents.Max(e => e.EventId)
            });
        }

        // Wait for new events
        var tcs = new TaskCompletionSource<bool>();
        var requestId = DateTime.UtcNow.Ticks;

        lock (_pollLock)
        {
            _pollWaiters[requestId] = tcs;
        }

        try
        {
            // Wait for either new events or timeout
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeout)));

            // Check again for new events
            newEvents = _eventBuffer.Where(e => e.EventId > lastEventId).ToList();
            if (newEvents.Any())
            {
                return Ok(new
                {
                    events = newEvents,
                    count = newEvents.Count,
                    lastEventId = newEvents.Max(e => e.EventId)
                });
            }

            // Timeout with no new events
            return Ok(new
            {
                events = Array.Empty<DriverIdEvent>(),
                count = 0,
                lastEventId
            });
        }
        finally
        {
            lock (_pollLock)
            {
                _pollWaiters.Remove(requestId);
            }
        }
    }

    /// <summary>
    /// GET /api/driverid/{apiKey}/health
    /// Health check endpoint
    /// </summary>
    [HttpGet("health")]
    public IActionResult GetHealth(string apiKey)
    {
        var validationResult = ValidateApiKey(apiKey);
        if (validationResult != null) return validationResult;

        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            bufferedEvents = _eventBuffer.Count
        });
    }

    /// <summary>
    /// GET /api/driverid/{apiKey}/lookup/{driverId}
    /// Lookup driver by ID
    /// </summary>
    [HttpGet("lookup/{driverId}")]
    public IActionResult LookupDriver(string apiKey, long driverId)
    {
        var validationResult = ValidateApiKey(apiKey);
        if (validationResult != null) return validationResult;

        var mapping = _driverMappings.FirstOrDefault(m => m.DriverId == driverId);
        if (mapping == null)
        {
            return NotFound(new { error = $"Driver {driverId} not found" });
        }

        return Ok(mapping);
    }

    /// <summary>
    /// GET /api/driverid/{apiKey}/mappings
    /// Get all driver mappings
    /// </summary>
    [HttpGet("mappings")]
    public IActionResult GetAllMappings(string apiKey)
    {
        var validationResult = ValidateApiKey(apiKey);
        if (validationResult != null) return validationResult;

        return Ok(new
        {
            count = _driverMappings.Count,
            mappings = _driverMappings
        });
    }

    /// <summary>
    /// POST /api/driverid/{apiKey}/simulate
    /// Helper endpoint to trigger a new fake event (for testing)
    /// </summary>
    [HttpPost("simulate")]
    public IActionResult SimulateEvent(string apiKey, [FromBody] SimulateEventRequest request)
    {
        var validationResult = ValidateApiKey(apiKey);
        if (validationResult != null) return validationResult;

        var mapping = _driverMappings.FirstOrDefault(m => m.DriverId == request.DriverId);
        if (mapping == null)
        {
            return NotFound(new { error = $"Driver {request.DriverId} not found" });
        }

        AddFakeEvent(
            request.DriverId,
            request.DriverName ?? $"Driver {request.DriverId}",
            request.CarNumber ?? "0",
            request.Ft200DeviceId ?? 1001,
            mapping.RfidTag,
            mapping.BleMacAddress
        );

        return Ok(new { message = "Event simulated successfully", eventId = _nextEventId - 1 });
    }
}

#region Models

public class DriverIdEvent
{
    public long EventId { get; set; }
    public DateTime Timestamp { get; set; }
    public long DriverId { get; set; }
    public string DriverName { get; set; } = string.Empty;
    public string CarNumber { get; set; } = string.Empty;
    public int Ft200DeviceId { get; set; }
    public string Rfid { get; set; } = string.Empty;
    public string BleMac { get; set; } = string.Empty;
    public bool DeviceLookupFound { get; set; }
}

public class DriverMapping
{
    public long DriverId { get; set; }
    public string RfidTag { get; set; } = string.Empty;
    public string BleMacAddress { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class SimulateEventRequest
{
    public long DriverId { get; set; }
    public string? DriverName { get; set; }
    public string? CarNumber { get; set; }
    public int? Ft200DeviceId { get; set; }
}

#endregion
