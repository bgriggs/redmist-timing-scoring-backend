using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Mappers;
using System.Text.Json;

namespace RedMist.EventProcessor.EventStatus.SessionMonitoring;

public class SessionMonitorV2 : BackgroundService
{
    private ILogger Logger { get; }
    public SessionMonitor InnerSessionMonitor { get; }
    private readonly SessionContext sessionContext;
    private Session? lastSession;
    private SessionState? last = null;


    public SessionMonitorV2(IConfiguration configuration, IDbContextFactory<TsContext> tsContext,
        ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        var eventId = configuration.GetValue("event_id", 0);
        InnerSessionMonitor = new SessionMonitor(eventId, tsContext, loggerFactory, sessionContext);
        InnerSessionMonitor.FinalizedSession += Sm_FinalizedSession;
        this.sessionContext = sessionContext;
    }


    public async Task Process(TimingMessage tm)
    {
        if (tm.Type != Backend.Shared.Consts.EVENT_SESSION_CHANGED_TYPE)
            return;

        lastSession = JsonSerializer.Deserialize<Session>(tm.Data);
        if (lastSession != null)
        {
            await InnerSessionMonitor.ProcessAsync(lastSession.Id, sessionContext.CancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            sessionContext.SetSessionClassMetadata();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error setting session class metadata");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await RunCheckForFinished(stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading event status stream");
            }
        }
    }

    public async Task RunCheckForFinished(CancellationToken stoppingToken)
    {
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(stoppingToken))
        {
            if (last != null)
            {
                var pc = SessionStateMapper.ToPatch(sessionContext.SessionState);
                pc.CarPositions = null; // Don't need to keep car positions
                InnerSessionMonitor.CheckForFinished(last, SessionStateMapper.PatchToEntity(pc));
            }

            var pl = SessionStateMapper.ToPatch(sessionContext.SessionState);
            pl.CarPositions = null; // Don't need to keep car positions
            last = SessionStateMapper.PatchToEntity(pl);
        }
    }

    private async void Sm_FinalizedSession()
    {
        if (lastSession == null)
            return;
        await sessionContext.NewSession(lastSession.Id, lastSession.Name);
        sessionContext.SetSessionClassMetadata();
    }
}
