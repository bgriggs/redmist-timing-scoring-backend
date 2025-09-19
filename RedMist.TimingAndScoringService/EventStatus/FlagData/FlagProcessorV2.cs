using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus.FlagData.StateChanges;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus.FlagData;

public class FlagProcessorV2
{
    private ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SessionContext sessionContext;
    private readonly FlagProcessor flagProcessor;

    
    public FlagProcessorV2(IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        this.tsContext = tsContext ?? throw new ArgumentNullException(nameof(tsContext));
        Logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(GetType().Name);
        this.sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));

        flagProcessor = new FlagProcessor(sessionContext.SessionState.EventId, tsContext, loggerFactory);
    }


    public async Task<PatchUpdates?> Process(TimingMessage message)
    {
        if (message.Type != Backend.Shared.Consts.FLAGS_TYPE)
            return null;

        var fs = JsonSerializer.Deserialize<List<FlagDuration>>(message.Data);
        if (fs != null)
        {
            await flagProcessor.ProcessFlags(sessionContext.SessionState.SessionId, fs, sessionContext.CancellationToken);
            var flagDurations = await flagProcessor.GetFlagsAsync(sessionContext.CancellationToken);
            var flagChange = new FlagsStateChange(flagDurations);
            var sp = flagChange.ApplySessionChange(sessionContext.SessionState);
            if (sp != null)
                return new PatchUpdates([sp], []);
        }
        return null;
    }
}
