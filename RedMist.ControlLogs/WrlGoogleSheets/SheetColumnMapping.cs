using RedMist.TimingCommon.Models;
using System.Reflection;

namespace RedMist.ControlLogs.WrlGoogleSheets;

/// <summary>
/// Represents a WRL google sheet column mapping.
/// </summary>
public class SheetColumnMapping
{
    public string SheetColumn { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public Func<string, object>? Convert { get; set; }

    public bool SetEntryValue(ControlLogEntry entry, string value)
    {
        var prop = entry.GetType().GetProperty(PropertyName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null)
        {
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
}
