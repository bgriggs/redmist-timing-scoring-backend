namespace RedMist.Backend.Shared.Utilities;

public class SessionHelper
{
    /// <summary>
    /// Uses the name of a session to determine if it is a practice or qualifying session.
    /// This is an estimate only, based on whether the session name contains certain terms.
    /// </summary>
    /// <param name="sessionName"></param>
    /// <returns>true if it is practice or qualifying</returns>
    public static bool IsPracticeOrQualifyingSession(string sessionName)
    {
        if (string.IsNullOrEmpty(sessionName))
            return false;

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
