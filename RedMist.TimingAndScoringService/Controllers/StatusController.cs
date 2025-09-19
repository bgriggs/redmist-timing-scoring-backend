using MessagePack;
using Microsoft.AspNetCore.Mvc;
using RedMist.TimingAndScoringService.EventStatus;

namespace RedMist.TimingAndScoringService.Controllers;

[ApiController]
[Route("[controller]/[action]")]
public class StatusController(SessionContext sessionContext) : ControllerBase
{
    /// <summary>
    /// Gets current session status as a MessagePack serialized SessionState object.
    /// Ignores accept headers and always returns MessagePack.
    /// </summary>
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
