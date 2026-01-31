using RedMist.TimingCommon.Models;
using System.Linq.Expressions;
using System.Reflection;

namespace RedMist.ControlLogs;

/// <summary>
/// Represents a google sheet column mapping with cached reflection for performance.
/// </summary>
public class SheetColumnMapping
{
    private PropertyInfo? cachedProperty;
    private PropertyInfo? cachedHighlightProperty;
    private Action<ControlLogEntry, object>? cachedSetter;
    private Func<ControlLogEntry, object?>? cachedGetter;
    private Action<ControlLogEntry, bool>? cachedHighlightSetter;
    private string? cachedHighlightPropertyName;
    private bool isInitialized;

    public string SheetColumn { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public bool SetIfNotNullOrEmpty { get; set; } = false;
    public bool SetIfTargetNotNullOrEmpty { get; set; } = false;
    public Func<string, object>? Convert { get; set; }

    private void EnsureInitialized(ControlLogEntry entry)
    {
        if (isInitialized) return;

        var entryType = typeof(ControlLogEntry);
        cachedProperty = entryType.GetProperty(PropertyName, BindingFlags.Instance | BindingFlags.Public);

        if (cachedProperty != null)
        {
            // Create compiled setter delegate for better performance than reflection
            var entryParam = Expression.Parameter(typeof(ControlLogEntry), "entry");
            var valueParam = Expression.Parameter(typeof(object), "value");
            var convertedValue = Expression.Convert(valueParam, cachedProperty.PropertyType);
            var propertyExpr = Expression.Property(entryParam, cachedProperty);
            var assignExpr = Expression.Assign(propertyExpr, convertedValue);
            cachedSetter = Expression.Lambda<Action<ControlLogEntry, object>>(assignExpr, entryParam, valueParam).Compile();

            // Create compiled getter delegate if needed for SetIfTargetNotNullOrEmpty
            if (SetIfTargetNotNullOrEmpty)
            {
                var getPropertyExpr = Expression.Property(entryParam, cachedProperty);
                var convertToObject = Expression.Convert(getPropertyExpr, typeof(object));
                cachedGetter = Expression.Lambda<Func<ControlLogEntry, object?>>(convertToObject, entryParam).Compile();
            }

            // Cache highlight property if it exists
            cachedHighlightPropertyName = $"Is{PropertyName}Highlighted";
            cachedHighlightProperty = entryType.GetProperty(cachedHighlightPropertyName, BindingFlags.Instance | BindingFlags.Public);

            if (cachedHighlightProperty != null && cachedHighlightProperty.PropertyType == typeof(bool))
            {
                var highlightPropertyExpr = Expression.Property(entryParam, cachedHighlightProperty);
                var boolParam = Expression.Parameter(typeof(bool), "value");
                var highlightAssignExpr = Expression.Assign(highlightPropertyExpr, boolParam);
                cachedHighlightSetter = Expression.Lambda<Action<ControlLogEntry, bool>>(highlightAssignExpr, entryParam, boolParam).Compile();
            }
        }

        isInitialized = true;
    }

    public bool SetEntryValue(ControlLogEntry entry, string value)
    {
        EnsureInitialized(entry);

        if (cachedProperty == null || cachedSetter == null)
        {
            return false;
        }

        if (SetIfNotNullOrEmpty && string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (SetIfTargetNotNullOrEmpty && cachedGetter != null)
        {
            var existingValue = cachedGetter(entry);
            if (existingValue != null && !string.IsNullOrWhiteSpace(existingValue.ToString()))
            {
                return false;
            }
        }

        try
        {
            var valueToSet = Convert != null ? Convert(value) : value;
            cachedSetter(entry, valueToSet);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetCellHighlighted(ControlLogEntry entry)
    {
        EnsureInitialized(entry);
        cachedHighlightSetter?.Invoke(entry, true);
    }
}
