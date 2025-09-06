using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;

namespace RedMist.TimingAndScoringService.EventStatus.PositionEnricher;

public class PositionDataEnricher
{
    private ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SessionContext sessionContext;
    private readonly PositionMetadataProcessor positionMetadataProcessor = new();

    public PositionDataEnricher(IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
    {

        this.tsContext = tsContext ?? throw new ArgumentNullException(nameof(tsContext));
        Logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(GetType().Name);
        this.sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
    }


    public void UpdateCarPositions()
    {
        positionMetadataProcessor.UpdateCarPositions(sessionContext.SessionState.CarPositions);
    }

    public void Clear()
    {
        positionMetadataProcessor.Clear();
    }
}
