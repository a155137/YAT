namespace YAT.Application.Graphs;

// What the height of a histogram bar stands for.
public enum HistogramYScale
{
    // The number of observations in the bin.
    Frequency,

    // The share of its own series' observations in the bin, in percent: every series adds up to 100.
    Percent,

    // The share of its own series' observations in the bin, divided by the bin's width: every series has unit area.
    Density
}

// How a histogram's bins are chosen.
public enum HistogramBinningMode
{
    // As many bins as the data asks for, over the data's own range (the bin-count rules of HistogramBinCount).
    Auto,

    // BinCount bins over the data's own range.
    Count,

    // Bins of BinWidth whose edges are BinStart + k x BinWidth: a grid fixed by the options, not by the data, so two
    // histograms with the same width and start share their bins wherever their data overlaps.
    WidthAndStart
}

// What a histogram shows of its own plot: its bar heights and its bins. Like ProbabilityPlotOptions these are choices
// only the histogram's builder can make - they decide the bins, the counts' scale and the axes - so they are handed to
// that builder and nothing else. Only a graph type that declares GraphCapability.HistogramControls reads them; every
// other graph type ignores them, so a configuration can always carry the defaults.
//
// Only the values the chosen binning mode uses are read: BinCount for Count, BinWidth and BinStart for WidthAndStart.
// Whether they are usable is decided by HistogramOptionsRules.
//
// ShowNormalFit draws over each series the normal curve of its own mean and sample standard deviation, on the bars' Y
// scale (Task #042). It never changes the bins, the counts or the X axis.
public sealed record HistogramOptions(
    HistogramYScale YScale = HistogramYScale.Frequency,
    HistogramBinningMode BinningMode = HistogramBinningMode.Auto,
    int? BinCount = null,
    double? BinWidth = null,
    double? BinStart = null,
    bool ShowNormalFit = false)
{
    // The most bins a histogram draws, however they are chosen. The same limit the automatic bin count keeps to.
    public const int MaximumBinCount = 200;

    public const int MinimumBinCount = 1;

    // Frequency over automatic bins, without a normal fit: the histogram as it always was.
    public static HistogramOptions Default { get; } = new();
}

// One reason histogram options cannot be used.
public enum HistogramOptionsProblem
{
    // The Y scale or the binning mode is not one of the defined values.
    UnknownChoice,

    // Count binning without a whole number of bins from MinimumBinCount to MaximumBinCount.
    BinCountInvalid,

    // Width-and-start binning without a finite width greater than zero.
    BinWidthInvalid,

    // Width-and-start binning without a finite start.
    BinStartInvalid
}

// The rules histogram options obey before any data is seen. How many bins a width actually needs depends on the data,
// so that is checked when the histogram is prepared, not here.
public static class HistogramOptionsRules
{
    public static IReadOnlyList<HistogramOptionsProblem> Check(HistogramOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<HistogramOptionsProblem>();
        if (!Enum.IsDefined(options.YScale) || !Enum.IsDefined(options.BinningMode))
        {
            problems.Add(HistogramOptionsProblem.UnknownChoice);
            return problems;
        }

        switch (options.BinningMode)
        {
            case HistogramBinningMode.Count:
                if (options.BinCount is not (>= HistogramOptions.MinimumBinCount and <= HistogramOptions.MaximumBinCount))
                {
                    problems.Add(HistogramOptionsProblem.BinCountInvalid);
                }

                break;

            case HistogramBinningMode.WidthAndStart:
                if (options.BinWidth is not { } width || !double.IsFinite(width) || width <= 0)
                {
                    problems.Add(HistogramOptionsProblem.BinWidthInvalid);
                }

                if (options.BinStart is not { } start || !double.IsFinite(start))
                {
                    problems.Add(HistogramOptionsProblem.BinStartInvalid);
                }

                break;
        }

        return problems;
    }

    public static bool IsValid(HistogramOptions options) => Check(options).Count == 0;
}
