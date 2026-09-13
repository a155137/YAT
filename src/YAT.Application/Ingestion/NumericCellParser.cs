using System.Globalization;

namespace YAT.Application.Ingestion;

// The single numeric cell rule shared by type detection and paste execution, so both always agree.
// Invariant culture keeps parsing independent of Windows regional settings. Decimal point and exponent are
// allowed; thousands separators are not. NaN, Infinity and out-of-range values are not numeric.
internal static class NumericCellParser
{
    public static bool TryParse(string cell, out double value) =>
        double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
