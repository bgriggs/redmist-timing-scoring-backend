using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.Database;
using RedMist.TimingCommon.Models;

namespace RedMist.ControlLogs.WrlGoogleSheets;

public class GoogleSheetsControlLog : GoogleSheetsControlLogBase
{
    public override string Type => ControlLogType.WRL_GOOGLE_SHEET;

    //Time    Corner  Car #   Car #   Note    Status  Penalty / Action        Other Notes
    protected override SheetColumnMapping[] ColumnMappings { get; } =
    [
        new SheetColumnMapping{ SheetColumn = "Time", PropertyName = "Timestamp", IsRequired = true, Convert = (s) =>  { DateTime.TryParse(s, out var dt); return dt; } },
        new SheetColumnMapping{ SheetColumn = "Corner", PropertyName = "Corner", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Car #", PropertyName = "Car1", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Car #", PropertyName = "Car2", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Note", PropertyName = "Note", IsRequired = true },
        new SheetColumnMapping{ SheetColumn = "Status", PropertyName = "Status", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Penalty / Action", PropertyName = "PenaltyAction", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Other Notes", PropertyName = "OtherNotes", IsRequired = false },
    ];

    protected override string CellRange => "A4:H1000";

    public GoogleSheetsControlLog(ILoggerFactory loggerFactory, IConfiguration config, IDbContextFactory<TsContext> tsContext)
        : base(loggerFactory, config, tsContext)
    {
    }
}
