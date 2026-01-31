using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.Database;

namespace RedMist.ControlLogs.ChampCarGoogleSheets;

/// <summary>
/// Control log processing for ChampCar.
/// </summary>
/// <see cref="https://docs.google.com/spreadsheets/d/1qfBq0elogpzBkLRRR_X2OhfEfoORWfcNFTXzPpr7p8Q/edit?gid=300370005#gid=300370005"/>
public class GoogleSheetsControlLog : GoogleSheetsControlLogBase
{
    public override string Type => ControlLogType.CHAMPCAR_GOOGLE_SHEET;

    //Time    Car #   Car #   Corner  Cause    Pit-In   Penalty / Action    Status    Other Notes
    protected override SheetColumnMapping[] ColumnMappings { get; } =
    [
        new SheetColumnMapping{ SheetColumn = "Time", PropertyName = "Timestamp", IsRequired = true, Convert = (s) =>  { DateTime.TryParse(s, out var dt); return dt; } },
        new SheetColumnMapping{ SheetColumn = "Car #", PropertyName = "Car1", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Car #", PropertyName = "Car2", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Corner", PropertyName = "Corner", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Cause", PropertyName = "Note", IsRequired = false, SetIfNotNullOrEmpty = true },
        new SheetColumnMapping{ SheetColumn = "Flag State", PropertyName = "Note", IsRequired = false, SetIfNotNullOrEmpty = true, SetIfTargetNotNullOrEmpty = true },
        new SheetColumnMapping{ SheetColumn = "Status", PropertyName = "Status", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Penalty / Action", PropertyName = "PenalityAction", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Other Notes", PropertyName = "OtherNotes", IsRequired = false },
    ];

    protected override string CellRange => "A1:J1000";

    public GoogleSheetsControlLog(ILoggerFactory loggerFactory, IConfiguration config, IDbContextFactory<TsContext> tsContext)
        : base(loggerFactory, config, tsContext)
    {
    }
}
