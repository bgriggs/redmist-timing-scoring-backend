using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Processes competitor metadata, e.g. name, make, model, hometown, etc. for a specific event.
/// </summary>
public class CompetitorMetadataProcessor
{
    private ILogger Logger { get; }
    private readonly int eventId;
    private readonly IDbContextFactory<TsContext> tsContext;
    //private readonly Dictionary<string, CompetitorMetadata> competitorMetadataLookup = [];
    //private readonly SemaphoreSlim competitorMetadataLookupLock = new(1, 1);


    public CompetitorMetadataProcessor(int eventId, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory)
    {
        this.eventId = eventId;
        this.tsContext = tsContext;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    public async Task Process(List<CompetitorMetadata> competitorMetadata, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Processing {Count} competitor metadata entries for event {EventId}", competitorMetadata.Count, eventId);
        
        using var db = await tsContext.CreateDbContextAsync(cancellationToken);
        foreach (var competitor in competitorMetadata)
        {
            var existingCompetitor = await db.CompetitorMetadata
                .FirstOrDefaultAsync(c => c.EventId == eventId && c.CarNumber == competitor.CarNumber, cancellationToken);
            if (existingCompetitor != null)
            {
                if (competitor.LastUpdated > existingCompetitor.LastUpdated)
                {
                    db.Entry(existingCompetitor).CurrentValues.SetValues(competitor);
                }
            }
            else
            {
                await db.CompetitorMetadata.AddAsync(competitor, cancellationToken);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CompetitorMetadata> LoadCompetitorMetadata(string carNumber, CancellationToken cancellationToken)
    {
        using var db = await tsContext.CreateDbContextAsync(cancellationToken);
        var competitor = await db.CompetitorMetadata
            .FirstOrDefaultAsync(c => c.EventId == eventId && c.CarNumber == carNumber, cancellationToken);
        return competitor ?? new CompetitorMetadata { EventId = eventId, CarNumber = carNumber };
    }
}
