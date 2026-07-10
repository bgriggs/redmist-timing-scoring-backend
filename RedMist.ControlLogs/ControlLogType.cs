namespace RedMist.ControlLogs;

public class ControlLogType
{
    public const string WRL_GOOGLE_SHEET = "WrlGoogleSheet";
    public const string CHAMPCAR_GOOGLE_SHEET = "ChampCarGoogleSheet";
    public const string LUCKYDOG_GOOGLE_SHEET = "LuckyDogGoogleSheet";
    /// <summary>Race-control announcements from an external timing source (push stream, not a fetch).</summary>
    public const string ANNOUNCEMENT = "Announcement";

    public static readonly string[] Types = [WRL_GOOGLE_SHEET, CHAMPCAR_GOOGLE_SHEET, LUCKYDOG_GOOGLE_SHEET, ANNOUNCEMENT];
}
