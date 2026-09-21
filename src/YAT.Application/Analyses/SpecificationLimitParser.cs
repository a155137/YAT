using System.Globalization;

namespace YAT.Application.Analyses;

// Reads a specification limit the user typed. The rule is the one YAT parses every number with: invariant culture, so
// it does not depend on Windows regional settings, decimal point and exponent allowed, thousands separators not, and
// NaN and the infinities are not numbers a limit can be.
//
// Blank means "no limit on this side", which is how a one-sided specification is entered. Text that is not a number is
// a failure, never a silent 0.
public static class SpecificationLimitParser
{
    public static bool TryParse(string? text, out double? limit)
    {
        limit = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return false;
        }

        limit = value;
        return true;
    }
}
