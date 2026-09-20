using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;

namespace RedMist.EventLogger.Services;

/// <summary>
/// Whether this pod's event is a simulation, and so whether its viewers are real people.
/// </summary>
/// <remarks>
/// Load tests and relay rehearsals open hundreds of synthetic connections against simulation events.
/// Counting them would put fictional numbers into a real organization's report, so both halves of
/// viewership capture drop them - and they have to agree, or the half without the gate quietly
/// undoes the half with it.
/// </remarks>
public class SimulationGate(IDbContextFactory<TsContext> tsContext, HybridCache hcache, IConfiguration configuration)
{
    private const string CACHE_KEY = "event_simulation_{0}";

    private readonly int eventId = configuration.GetValue("event_id", 0);

    public async Task<bool> IsSimulationAsync(CancellationToken cancellationToken)
    {
        var key = string.Format(CACHE_KEY, eventId);
        return await hcache.GetOrCreateAsync(key, async cancel =>
        {
            await using var db = await tsContext.CreateDbContextAsync(cancel);
            return await db.Events.Where(e => e.Id == eventId).Select(e => e.IsSimulation).FirstOrDefaultAsync(cancel);
        }, cancellationToken: cancellationToken);
    }
}
