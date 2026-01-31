using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.Database;

namespace RedMist.ControlLogs.LuckyDogGoogleSheets;

/// <summary>
/// Control log processing for LuckyDog.
/// </summary>
/// <see cref="https://docs.google.com/spreadsheets/d/1wnTwrjS3BZo9akZN0QKFpq0udwu-cXBJa_LGsozOv5U/edit?gid=0#gid=0"/>
public class GoogleSheetsControlLog : GoogleSheetsControlLogBase
{
    public override string Type => ControlLogType.LUCKYDOG_GOOGLE_SHEET;

    //Time	Turn	Flag	Car	Condition	Note
    protected override SheetColumnMapping[] ColumnMappings { get; } =
    [
        new SheetColumnMapping{ SheetColumn = "Time", PropertyName = "Timestamp", IsRequired = true, Convert = (s) =>  { DateTime.TryParse(s, out var dt); return dt; } },
        new SheetColumnMapping{ SheetColumn = "Car", PropertyName = "Car1", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Turn", PropertyName = "Corner", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Condition", PropertyName = "Note", IsRequired = false, SetIfNotNullOrEmpty = true },
        new SheetColumnMapping{ SheetColumn = "Note", PropertyName = "OtherNotes", IsRequired = false },
    ];

    protected override string CellRange => "A1:F1000";

    public GoogleSheetsControlLog(ILoggerFactory loggerFactory, IConfiguration config, IDbContextFactory<TsContext> tsContext)
        : base(loggerFactory, config, tsContext)
    {
    }
}
