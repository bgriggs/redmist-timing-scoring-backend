using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.ControlLogs.WrlGoogleSheets;
using RedMist.Database;

namespace RedMist.ControlLogs;

public class ControlLogFactory(ILoggerFactory loggerFactory, IConfiguration config, IDbContextFactory<TsContext> tsContext) : IControlLogFactory
{
    private readonly ILoggerFactory loggerFactory = loggerFactory;
    private readonly IConfiguration config = config;
    private readonly IDbContextFactory<TsContext> tsContext = tsContext;

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

        throw new NotImplementedException($"type={type}");
    }
}
