using RedMist.TimingCommon.Models;
using System.Reflection;

namespace RedMist.ControlLogs;

/// <summary>
/// Represents a google sheet column mapping.
/// </summary>
public class SheetColumnMapping
{
    public string SheetColumn { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public bool SetIfNotNullOrEmpty { get; set; } = false;
    public bool SetIfTargetNotNullOrEmpty { get; set; } = false;
    public Func<string, object>? Convert { get; set; }

    public bool SetEntryValue(ControlLogEntry entry, string value)
    {
        var prop = entry.GetType().GetProperty(PropertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null)
        {
            if (SetIfNotNullOrEmpty)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    // Do not set if the value is null or whitespace
                    return false;
                }
            }

            if (SetIfTargetNotNullOrEmpty)
            {
                var v = prop.GetValue(entry);
                if (v != null && v?.ToString()?.Trim() != string.Empty)
                {
                    // Do not set the target if its value is not null or empty
                    return false;
                }
            }

            if (Convert != null)
            {
                try
                {
                    var v = Convert(value);
                    prop.SetValue(entry, v);
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                prop.SetValue(entry, value);
            }
            return true;
        }
        else
        {
            return false;
        }
    }

    public void SetCellHighlighted(ControlLogEntry entry)
    {
        var propName = $"Is{PropertyName}Highlighted";
        var prop = entry.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
        prop?.SetValue(entry, true);
    }
}
