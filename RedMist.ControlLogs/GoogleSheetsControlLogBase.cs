using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using System.Text;

namespace RedMist.ControlLogs;

/// <summary>
/// Base class for Google Sheets-based control log implementations.
/// Provides common functionality for loading and parsing control logs from Google Sheets.
/// </summary>
public abstract class GoogleSheetsControlLogBase : IControlLog
{
    protected IConfiguration Config { get; }
    protected ILogger Logger { get; }
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly SemaphoreSlim configLock = new(1, 1);
    private string? configJson;
    private ServiceAccountCredential? credential;
    private string? lastWorksheetParameter;
    private readonly Dictionary<int, SheetColumnMapping> columnIndexMappings = [];
    private bool disposed;

    /// <summary>
    /// Gets the type identifier for this control log implementation.
    /// </summary>
    public abstract string Type { get; }

    /// <summary>
    /// Gets the column mappings for the specific sheet structure.
    /// </summary>
    protected abstract SheetColumnMapping[] ColumnMappings { get; }

    /// <summary>
    /// Gets the cell range to read from the sheet (e.g., "A1:F1000", "A4:H1000").
    /// </summary>
    protected abstract string CellRange { get; }

    /// <summary>
    /// Gets the minimum year threshold for valid timestamps. Entries with timestamps before this year are filtered out.
    /// Default is 2025.
    /// </summary>
    protected virtual int MinimumTimestampYear => 2025;

    /// <summary>
    /// Gets the maximum number of consecutive missed timestamps before stopping parsing.
    /// Default is 2.
    /// </summary>
    protected virtual int MaxMissedTimestamps => 2;

    protected GoogleSheetsControlLogBase(ILoggerFactory loggerFactory, IConfiguration config, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        Config = config;
        this.tsContext = tsContext;
    }

    public async Task<(bool success, IEnumerable<ControlLogEntry> logs)> LoadControlLogAsync(string parameter, CancellationToken stoppingToken = default)
    {
        var paramParts = parameter.Split(';');
        if (paramParts.Length != 2)
        {
            Logger.LogError("Invalid parameter format for control log: {p}", parameter);
            return (false, []);
        }

        await configLock.WaitAsync(stoppingToken);
        try
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

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(configJson));
                credential = ServiceAccountCredential.FromServiceAccountData(stream);
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
        }
        finally
        {
            configLock.Release();
        }

        using var sheetsService = new SheetsService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = "RedMist"
        });

        var request = sheetsService.Spreadsheets.Get(paramParts[0]);
        request.IncludeGridData = true;

        var range = CellRange;
        if (paramParts.Length == 2 && !string.IsNullOrWhiteSpace(paramParts[1]))
        {
            range = $"{paramParts[1]}!{range}";
        }
        request.Ranges = range;

        var log = new List<ControlLogEntry>();
        try
        {
            var response = await request.ExecuteAsync(stoppingToken);

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

            for (int row = 1; row < response.Sheets[0].Data[0].RowData.Count; row++)
            {
                var rowData = response.Sheets[0].Data[0].RowData[row];
                if (rowData.Values == null)
                {
                    continue;
                }

                var requiredColumns = ColumnMappings.Where(c => c.IsRequired).ToList();
                ControlLogEntry entry = new() { OrderId = row };

                for (int col = 0; col < rowData.Values.Count; col++)
                {
                    if (columnIndexMappings.TryGetValue(col, out var mapping))
                    {
                        var cell = rowData.Values[col];
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
                                if (mapping.PropertyName.Contains("Time"))
                                {
                                    missedTimestampCount++;
                                }
                            }
                        }
                    }
                }

                if (requiredColumns.Count == 0 && entry.Timestamp > new DateTime(MinimumTimestampYear))
                {
                    log.Add(entry);
                }

                if (missedTimestampCount > MaxMissedTimestamps)
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
                map = ColumnMappings.LastOrDefault(c => string.Compare(c.SheetColumn, col, true) == 0);
            }
            else
            {
                map = ColumnMappings.FirstOrDefault(c => string.Compare(c.SheetColumn, col, true) == 0);
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
        foreach (var requiredHeader in ColumnMappings.Where(c => c.IsRequired))
        {
            bool found = columnIndexMappings.Values.Any(c => c.PropertyName == requiredHeader.PropertyName);
            if (!found)
            {
                Logger.LogError($"Required column '{requiredHeader.SheetColumn}'->'{requiredHeader.PropertyName}' not found");
                columnIndexMappings.Clear();
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                configLock?.Dispose();
            }
            disposed = true;
        }
    }
}
