using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.EventStatus.SessionMonitoring;

public class SessionMonitorV2
{
    private readonly SessionMonitor sm;
    private readonly SessionContext sessionContext;
    private Session? lastSession;


    public SessionMonitorV2(IConfiguration configuration, IDbContextFactory<TsContext> tsContext, ILoggerFactory loggerFactory, SessionContext sessionContext)
    {
        var eventId = configuration.GetValue("event_id", 0);
        sm = new SessionMonitor(eventId, tsContext, loggerFactory);
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
            await sm.ProcessSessionAsync(lastSession.Id, sessionContext.CancellationToken);
        }
    }

    private async void Sm_FinalizedSession()
    {
        if (lastSession == null)
            return;
        await sessionContext.NewSession(lastSession.Id, lastSession.Name);
    }
}
