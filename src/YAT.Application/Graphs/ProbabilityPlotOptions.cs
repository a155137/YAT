namespace YAT.Application.Graphs;

// What a normal probability plot shows of its own plot, as opposed to what every graph shows around it
// (GraphStatisticsOptions). These choices are made by the probability plot's builder, before the frame exists: a
// fitted line is part of the plot and reaches into the axis the builder chooses, so it cannot be added or removed
// afterwards.
//
// Only a graph type that declares GraphCapability.FittedLine reads it; every other graph type ignores it, so a
// configuration can always carry the defaults.
public sealed record ProbabilityPlotOptions(bool ShowFittedLine = true)
{
    // The fitted line is shown unless the user turns it off.
    public static ProbabilityPlotOptions Default { get; } = new();
}
