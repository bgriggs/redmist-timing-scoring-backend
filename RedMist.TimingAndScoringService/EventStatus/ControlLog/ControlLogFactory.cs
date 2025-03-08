using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus.ControlLog.WrlGoogleSheets;

namespace RedMist.TimingAndScoringService.EventStatus.ControlLog;

public class ControlLogFactory : IControlLogFactory
{
    private readonly ILoggerFactory loggerFactory;
    private readonly IConfiguration config;
    private readonly IDbContextFactory<TsContext> tsContext;

    public ControlLogFactory(ILoggerFactory loggerFactory, IConfiguration config, IDbContextFactory<TsContext> tsContext)
    {
        this.loggerFactory = loggerFactory;
        this.config = config;
        this.tsContext = tsContext;
    }


    public IControlLog CreateControlLog(string type)
    {
        if (type == ControlLogType.WRL_GOOGLE_SHEET)
        {
            return new GoogleSheetsControlLog(loggerFactory, config, tsContext);
        }

        throw new NotImplementedException($"type={type}");
    }
}
