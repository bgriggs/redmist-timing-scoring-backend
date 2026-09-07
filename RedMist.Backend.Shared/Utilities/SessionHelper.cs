namespace RedMist.Backend.Shared.Utilities;

public class SessionHelper
{
    /// <summary>
    /// Uses the name of a session to determine if it is a non-competitive session, i.e. anything
    /// without a race result: practice, qualifying, driver education (DE/HPDE), schools, open
    /// track time, and test or setup days.
    /// This is an estimate only, based on whether the session name contains certain terms. Session
    /// names are free text entered by the organizer, so a race can be named anything ("The New
    /// England Enduro", "Cookie Cutter Classic") and no term list can be exhaustive. Treat a false
    /// result as "probably a race" rather than a guarantee.
    /// </summary>
    /// <param name="sessionName">Free-text session name from the timing source.</param>
    /// <returns>true if it is practice, qualifying, driver education, a school, or a test session</returns>
    public static bool IsPracticeOrQualifyingSession(string sessionName)
    {
        if (string.IsNullOrEmpty(sessionName))
            return false;

        foreach (var term in Consts.PRACTICE_QUAL_TERMS)
        {
            if (sessionName.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Whole-word terms are matched exactly and case sensitively against whitespace-delimited
        // words. See Consts.PRACTICE_QUAL_WORD_TERMS for why they cannot be substring matched.
        foreach (var word in sessionName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var term in Consts.PRACTICE_QUAL_WORD_TERMS)
            {
                if (string.Equals(word, term, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}
