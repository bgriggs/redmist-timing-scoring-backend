namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Orders car numbers the way a person reads them rather than the way a database sorts them.
/// Car numbers are text because they can carry letters - "99x", "7A" - so an ordinal sort puts
/// "12" before "2" and "99x" before "9". This splits the leading digits off and compares those
/// numerically, falling back to an ordinal comparison of whatever follows.
/// </summary>
public sealed class CarNumberComparer : IComparer<string>
{
    /// <summary>A shared instance; the comparer holds no state.</summary>
    public static readonly CarNumberComparer Instance = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;

        var xDigits = LeadingDigitCount(x);
        var yDigits = LeadingDigitCount(y);

        // A car number with no leading digits ("Course Car") has nothing numeric to compare, so it
        // sorts after every numbered car rather than being forced to a value of zero.
        if (xDigits == 0 || yDigits == 0)
        {
            if (xDigits != yDigits)
                return xDigits == 0 ? 1 : -1;
            return string.CompareOrdinal(x, y);
        }

        // long rather than int: the number is free-form text from the timing system and there is
        // nothing stopping it being absurdly long, and overflowing here would scramble the order.
        if (long.TryParse(x.AsSpan(0, xDigits), out var xNum) && long.TryParse(y.AsSpan(0, yDigits), out var yNum))
        {
            var byNumber = xNum.CompareTo(yNum);
            if (byNumber != 0)
                return byNumber;
        }
        else
        {
            // Too many digits to fit a long. Longer runs of digits are larger numbers, except for
            // leading zeros, which are rare enough here not to be worth unpicking.
            var byLength = xDigits.CompareTo(yDigits);
            if (byLength != 0)
                return byLength;
        }

        // Same number: "9" before "9x", "9x" before "9y".
        return string.CompareOrdinal(x[xDigits..], y[yDigits..]);
    }

    private static int LeadingDigitCount(string value)
    {
        var i = 0;
        while (i < value.Length && char.IsAsciiDigit(value[i]))
            i++;
        return i;
    }
}
