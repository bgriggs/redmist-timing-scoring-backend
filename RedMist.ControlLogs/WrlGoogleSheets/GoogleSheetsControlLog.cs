using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.Database;
using RedMist.TimingCommon.Models;

namespace RedMist.ControlLogs.WrlGoogleSheets;

public class GoogleSheetsControlLog : IControlLog
{
    private IConfiguration Config { get; }
    private ILogger Logger { get; }
    public string Type => ControlLogType.WRL_GOOGLE_SHEET;


    //Time    Corner  Car #   Car #   Note    Status  Penalty / Action        Other Notes
    private static readonly SheetColumnMapping[] columns =
    [
        new SheetColumnMapping{ SheetColumn = "Time", PropertyName = "Timestamp", IsRequired = true, Convert = (s) => DateTime.Parse(s) },
        new SheetColumnMapping{ SheetColumn = "Corner", PropertyName = "Corner", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Car #", PropertyName = "Car1", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Car #", PropertyName = "Car2", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Note", PropertyName = "Note", IsRequired = true },
        new SheetColumnMapping{ SheetColumn = "Status", PropertyName = "Status", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Penalty / Action", PropertyName = "PenalityAction", IsRequired = false },
        new SheetColumnMapping{ SheetColumn = "Other Notes", PropertyName = "OtherNotes", IsRequired = false },
    ];

    private readonly Dictionary<int, SheetColumnMapping> columnIndexMappings = [];
    private readonly IDbContextFactory<TsContext> tsContext;
    private string? configJson;

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
                Logger.LogError("Unable to find control log configuration for sheet {0}", parameter);
                return (false, []);
            }
            configJson = eventConfig.Json;
        }

        var paramParts = parameter.Split(';');
        if (paramParts.Length != 2)
        {
            Logger.LogError("Invalid parameter format for control log: {0}", parameter);
            return (false, []);
        }

        var googleCreds = GoogleCredential.FromJson(configJson);
        var sheetsService = new SheetsService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = googleCreds,
            ApplicationName = "RedMist"
        });

        var vals = sheetsService.Spreadsheets.Values;
        var range = "A4:H1000";
        if (paramParts.Length == 2 && !string.IsNullOrWhiteSpace(paramParts[1]))
        {
            // Parameter is the sheet title
            range = $"{paramParts[1]}!{range}";
        }
        else
        {
            range = $"{range}";
        }

        var log = new List<ControlLogEntry>();
        try
        {
            var request = vals.Get(paramParts[0], range);
            var response = await request.ExecuteAsync(stoppingToken);

            // Check for column mappings
            if (columnIndexMappings.Count == 0)
            {
                var header = response.Values[0];
                InitializeColumnMappings(header);
            }
            if (columnIndexMappings.Count == 0)
            {
                return (false, []);
            }

            // Parse the log, skip the header
            for (int row = 1; row < response.Values.Count; row++)
            {
                var requiredColumns = columns.Where(c => c.IsRequired).ToList();
                ControlLogEntry entry = new() { OrderId = row };
                for (int col = 0; col < response.Values[row].Count; col++)
                {
                    if (columnIndexMappings.TryGetValue(col, out var mapping))
                    {
                        var cell = response.Values[row][col].ToString();
                        if (cell != null)
                        {
                            var valueSet = mapping.SetEntryValue(entry, cell);
                            if (valueSet)
                            {
                                requiredColumns.Remove(mapping);
                            }
                            else
                            {
                                Logger.LogTrace($"Failed to parse and assign row {row + 4}, {mapping.PropertyName}, value='{cell}'");
                            }
                        }
                    }
                }

                // Make sure all required columns have been found
                if (requiredColumns.Count == 0)
                {
                    log.Add(entry);
                }
                else
                {
                    foreach (var column in requiredColumns)
                    {
                        Logger.LogTrace($"Row {row + 4} did not pass validation, missing {column.PropertyName}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading control log from Google Sheets: {0}", parameter);
            return (false, []);
        }
        return (true, log);
    }

    private void InitializeColumnMappings(IList<object> header)
    {
        for (int i = 0; i < header.Count; i++)
        {
            SheetColumnMapping? map;
            var col = header[i].ToString() ?? string.Empty;

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
