using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.Database;
using RedMist.TimingCommon.Models;

namespace RedMist.ControlLogs.ChampCarGoogleSheets;

/// <summary>
/// Control log processing for ChampCar.
/// </summary>
/// <see cref="https://docs.google.com/spreadsheets/d/1qfBq0elogpzBkLRRR_X2OhfEfoORWfcNFTXzPpr7p8Q/edit?gid=300370005#gid=300370005"/>
public class GoogleSheetsControlLog : IControlLog
{
    private IConfiguration Config { get; }
    private ILogger Logger { get; }
    public string Type => ControlLogType.CHAMPCAR_GOOGLE_SHEET;


    //Time    Car #   Car #   Corner  Cause    Pit-In   Penalty / Action    Status    Other Notes
    private static readonly SheetColumnMapping[] columns =
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

    private readonly Dictionary<int, SheetColumnMapping> columnIndexMappings = [];
    private readonly IDbContextFactory<TsContext> tsContext;
    private string? configJson;
    private string? lastWorksheetParameter; // Track the last worksheet parameter to detect changes


    public GoogleSheetsControlLog(ILoggerFactory loggerFactory, IConfiguration config, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        Config = config;
        this.tsContext = tsContext;
    }


    public async Task<(bool success, IEnumerable<ControlLogEntry> logs)> LoadControlLogAsync(string parameter, CancellationToken stoppingToken = default)
    {
        if (configJson == null)
        {
            using var db = await tsContext.CreateDbContextAsync(stoppingToken);
            var eventConfig = await db.GoogleSheetsConfigs.FirstOrDefaultAsync(stoppingToken);
            if (eventConfig == null)
            {
                Logger.LogError("Unable to find control log configuration for sheet {p}", parameter);
                return (false, []);
            }
            configJson = eventConfig.Json;
        }

        var paramParts = parameter.Split(';');
        if (paramParts.Length != 2)
        {
            Logger.LogError("Invalid parameter format for control log: {p}", parameter);
            return (false, []);
        }

        if (lastWorksheetParameter != parameter)
        {
            if (lastWorksheetParameter != null)
            {
                Logger.LogInformation("Worksheet parameter changed from '{oldParam}' to '{newParam}', clearing column mappings", lastWorksheetParameter, parameter);
            }
            columnIndexMappings.Clear();
            lastWorksheetParameter = parameter;
        }

        var googleCreds = GoogleCredential.FromJson(configJson);
        using var sheetsService = new SheetsService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = googleCreds,
            ApplicationName = "RedMist"
        });

        var request = sheetsService.Spreadsheets.Get(paramParts[0]);
        request.IncludeGridData = true;

        var range = "A1:J1000";
        if (paramParts.Length == 2 && !string.IsNullOrWhiteSpace(paramParts[1]))
        {
            // Parameter is the sheet title
            range = $"{paramParts[1]}!{range}";
        }
        else
        {
            range = $"{range}";
        }
        request.Ranges = range;

        var log = new List<ControlLogEntry>();
        try
        {
            var response = await request.ExecuteAsync(stoppingToken);

            // Check for column mappings
            if (columnIndexMappings.Count == 0)
            {
                var header = response.Sheets[0].Data[0].RowData[0];
                InitializeColumnMappings(header);
            }
            if (columnIndexMappings.Count == 0)
            {
                return (false, []);
            }

            int missedTimestampCount = 0;

            // Parse the log, skip the header
            for (int row = 1; row < response.Sheets[0].Data[0].RowData.Count; row++)
            {
                var requiredColumns = columns.Where(c => c.IsRequired).ToList();
                ControlLogEntry entry = new() { OrderId = row };
                for (int col = 0; col < response.Sheets[0].Data[0].RowData[row].Values.Count; col++)
                {
                    if (columnIndexMappings.TryGetValue(col, out var mapping))
                    {
                        var cell = response.Sheets[0].Data[0].RowData[row].Values[col];
                        if (cell != null)
                        {
                            var valueSet = mapping.SetEntryValue(entry, cell.FormattedValue);
                            if (valueSet)
                            {
                                requiredColumns.Remove(mapping);
                                var color = cell.EffectiveFormat?.BackgroundColor;
                                if (color != null && color.Red == 1 && color.Green == 1 && color.Blue == null)
                                {
                                    mapping.SetCellHighlighted(entry);
                                }
                            }
                            else
                            {
                                //Logger.LogTrace($"Failed to parse and assign row {row + 4}, {mapping.PropertyName}, value='{cell.FormattedValue}'");
                                if (mapping.PropertyName.Contains("Time"))
                                {
                                    missedTimestampCount++;
                                }
                            }
                        }
                    }
                }

                // Make sure all required columns have been found
                if (requiredColumns.Count == 0 && entry.Timestamp > new DateTime(2025))
                {
                    log.Add(entry);
                }
                else
                {
                    //foreach (var column in requiredColumns)
                    //{
                    //    Logger.LogTrace($"Row {row + 4} did not pass validation, missing {column.PropertyName}");
                    //}
                }

                if (missedTimestampCount > 2)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading control log from Google Sheets: {p}", parameter);
            return (false, []);
        }
        return (true, log);
    }

    private void InitializeColumnMappings(RowData header)
    {
        for (int i = 0; i < header.Values.Count; i++)
        {
            SheetColumnMapping? map;
            var col = header.Values[i].FormattedValue ?? string.Empty;

            // Since there are two identical Car # column headers, check whether the first one has already been used
            if (col.StartsWith("Car", StringComparison.InvariantCultureIgnoreCase) && columnIndexMappings.Values.Any(c => c.SheetColumn.StartsWith("Car")))
            {
                // Pick the 2nd mapping
                map = columns.LastOrDefault(c => string.Compare(c.SheetColumn, col, true) == 0);
            }
            else
            {
                map = columns.FirstOrDefault(c => string.Compare(c.SheetColumn, col, true) == 0);
            }

            if (map != null)
            {
                columnIndexMappings[i] = map;
            }
            else
            {
                Logger.LogWarning($"Unable to find a mapping for column '{col}' at index {i}");
            }
        }

        // Check for required headers
        foreach (var requiredHeader in columns.Where(c => c.IsRequired))
        {
            bool found = columnIndexMappings.Values.Any(c => c.PropertyName == requiredHeader.PropertyName);
            if (!found)
            {
                Logger.LogError($"Required column '{requiredHeader.SheetColumn}'->'{requiredHeader.PropertyName}' not found");
                columnIndexMappings.Clear();
            }
        }
    }
}
