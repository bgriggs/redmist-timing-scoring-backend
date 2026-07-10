using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.ControlLogs.Announcements;
using RedMist.ControlLogs.WrlGoogleSheets;
using RedMist.Database;

namespace RedMist.ControlLogs;

public class ControlLogFactory(ILoggerFactory loggerFactory, IConfiguration config, IDbContextFactory<TsContext> tsContext,
    IAnnouncementControlLogStore? announcementStore = null) : IControlLogFactory
{
    private readonly ILoggerFactory loggerFactory = loggerFactory;
    private readonly IConfiguration config = config;
    private readonly IDbContextFactory<TsContext> tsContext = tsContext;
    // Optional: only the ControlLogProcessor host registers/feeds this store via its stream consumer.
    // Other hosts (e.g. EventManagement stats) resolve the factory without it and get an empty log.
    private readonly IAnnouncementControlLogStore announcementStore = announcementStore ?? new AnnouncementControlLogStore();

    public IControlLog CreateControlLog(string type)
    {
        if (type == ControlLogType.WRL_GOOGLE_SHEET)
        {
            return new GoogleSheetsControlLog(loggerFactory, config, tsContext);
        }
        else if (type == ControlLogType.CHAMPCAR_GOOGLE_SHEET)
        {
            return new ChampCarGoogleSheets.GoogleSheetsControlLog(loggerFactory, config, tsContext);
        }
        else if (type == ControlLogType.LUCKYDOG_GOOGLE_SHEET)
        {
            return new LuckyDogGoogleSheets.GoogleSheetsControlLog(loggerFactory, config, tsContext);
        }
        else if (type == ControlLogType.ANNOUNCEMENT)
        {
            return new AnnouncementControlLog(announcementStore);
        }

        throw new NotImplementedException($"type={type}");
    }
}
