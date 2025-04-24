namespace RedMist.TimingAndScoringService.Utilities;

public class SessionHelper
{
    public static bool IsPracticeOrQualifyingSession(string sessionName)
    {
        bool isPracticeOrQual = false;
        foreach (var term in Consts.PRACTICE_QUAL_TERMS)
        {
            if (sessionName.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                isPracticeOrQual = true;
                break;
            }
        }

        return isPracticeOrQual;
    }
}
