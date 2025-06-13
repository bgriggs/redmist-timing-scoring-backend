namespace RedMist.ControlLogs;

public class ControlLogType
{
    public const string WRL_GOOGLE_SHEET = "WrlGoogleSheet"; 
    public const string CHAMPCAR_GOOGLE_SHEET = "ChampCarGoogleSheet";

    public static readonly string[] Types = [WRL_GOOGLE_SHEET, CHAMPCAR_GOOGLE_SHEET];
}
