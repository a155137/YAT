using System.Globalization;

namespace YAT.app.Analyses;

// How an analysis result shows numbers. One place, so every analysis table reads the same way.
public static class AnalysisNumberFormat
{
    // Statistics are shown with eight significant digits in the invariant culture: enough to tell measured parts
    // apart, short enough to scan a column of them, and switching to exponent notation only when the value needs it
    // (0.33333333, 1.57E-06, 14983.247).
    private const string StatisticFormat = "G8";

    // Numeric group values are labelled exactly as the graphs label them, so the same SITE column reads the same way
    // in a histogram legend and in a statistics table.
    private const string GroupValueFormat = "0.####";

    // A statistic a sample does not support is blank: an empty cell says "no value", a 0 would claim one.
    public static string Statistic(double? value) =>
        value is { } number ? number.ToString(StatisticFormat, CultureInfo.InvariantCulture) : string.Empty;

    // Counts (N, Missing) are whole numbers and are always shown, including 0.
    public static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string GroupValue(double value) => value.ToString(GroupValueFormat, CultureInfo.InvariantCulture);
}
