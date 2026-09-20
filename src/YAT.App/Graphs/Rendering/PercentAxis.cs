using System.Globalization;

namespace YAT.app.Graphs.Rendering;

// A plain percentage axis: nought to a hundred, read linearly.
//
// This is not the probability plot's axis. There a percentage is placed at the normal score it belongs to, which
// stretches the tails; here a percentage means the share of the observations it says, and equal shares take equal room.
public static class PercentAxis
{
    public const string Title = "Percent";

    public const double Minimum = 0;

    public const double Maximum = 100;

    // Every twenty percent: enough to read a cumulative curve by, without crowding the axis.
    public static readonly IReadOnlyList<double> Percents = [0, 20, 40, 60, 80, 100];

    public static GraphAxisModel Axis()
    {
        var ticks = new List<GraphAxisTick>(Percents.Count);
        foreach (var percent in Percents)
        {
            ticks.Add(new GraphAxisTick(percent, percent.ToString("0.####", CultureInfo.InvariantCulture)));
        }

        return new GraphAxisModel(new GraphAxisRange(Minimum, Maximum), ticks, Title);
    }
}
