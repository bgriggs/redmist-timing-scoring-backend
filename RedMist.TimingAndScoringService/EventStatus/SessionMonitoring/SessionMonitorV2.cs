using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Mappers;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus.SessionMonitoring;

public class SessionMonitorV2 : BackgroundService
{
    private ILogger Logger { get; }
    private readonly SessionMonitor sm;
    private readonly SessionContext sessionContext;
    private Session? lastSession;


    public SessionMonitorV2(IConfiguration configuration, IDbContextFactory<TsContext> tsContext, 
        ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        var eventId = configuration.GetValue("event_id", 0);
        sm = new SessionMonitor(eventId, tsContext, loggerFactory, sessionContext);
        sm.FinalizedSession += Sm_FinalizedSession;
        this.sessionContext = sessionContext;
    }


    public async Task Process(TimingMessage tm)
    {
        if (tm.Type != Backend.Shared.Consts.EVENT_SESSION_CHANGED_TYPE)
            return;

        lastSession = JsonSerializer.Deserialize<Session>(tm.Data);
        if (lastSession != null)
        {
            await sm.ProcessAsync(lastSession.Id, sessionContext.CancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SessionState? last = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                using (await sessionContext.SessionStateLock.AcquireReadLockAsync(sessionContext.CancellationToken))
                {
                    if (last != null)
                    {
                        var pc = SessionStateMapper.ToPatch(sessionContext.SessionState);
                        pc.CarPositions = null; // Don't need to keep car positions
                        sm.CheckForFinished(last, SessionStateMapper.PatchToEntity(pc));
                    }

                    var pl = SessionStateMapper.ToPatch(sessionContext.SessionState);
                    pl.CarPositions = null; // Don't need to keep car positions
                    last = SessionStateMapper.PatchToEntity(pl);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
            }
        }
    }

    private async void Sm_FinalizedSession()
    {
        if (lastSession == null)
            return;
        await sessionContext.NewSession(lastSession.Id, lastSession.Name);
    }
}
