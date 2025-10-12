using MessagePack;
using Microsoft.AspNetCore.Mvc;
using RedMist.TimingAndScoringService.EventStatus;

namespace RedMist.TimingAndScoringService.Controllers;

/// <summary>
/// Internal status controller for the Timing and Scoring Service.
/// Provides real-time session state data in MessagePack format for efficient inter-service communication.
/// </summary>
/// <remarks>
/// This controller is used internally by other services (e.g., StatusApi) to retrieve current session state.
/// It does not require authentication as it's intended for internal service-to-service communication.
/// </remarks>
[ApiController]
[Route("[controller]/[action]")]
public class StatusController(SessionContext sessionContext) : ControllerBase
{
    /// <summary>
    /// Gets the current session status as a MessagePack-serialized SessionState object.
    /// </summary>
    /// <returns>MessagePack binary stream containing the current session state.</returns>
    /// <response code="200">Returns MessagePack binary data (application/x-msgpack).</response>
    /// <remarks>
    /// <para>This endpoint ignores Accept headers and always returns MessagePack format for maximum efficiency.</para>
    /// <para>The SessionState object contains all current timing data including car positions, flags, and event status.</para>
    /// <para>Thread-safe: Uses a read lock to ensure data consistency during serialization.</para>
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> GetStatus()
    {
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(sessionContext.CancellationToken))
        {
            var serialized = MessagePackSerializer.Serialize(sessionContext.SessionState);
            return File(serialized, "application/x-msgpack");
        }
    }
}
