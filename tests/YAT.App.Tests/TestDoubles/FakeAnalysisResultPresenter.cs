using YAT.app.Analyses;

namespace YAT.App.Tests.TestDoubles;

// Records the result tables that would have been shown in a window, so the path from the Statistics menu to a result
// can be tested without a window.
internal sealed class FakeAnalysisResultPresenter : IAnalysisResultPresenter
{
    public List<AnalysisResultTable> Shown { get; } = [];

    public AnalysisResultTable Last =>
        Shown.Count > 0 ? Shown[^1] : throw new InvalidOperationException("No analysis result was shown.");

    public void ShowResult(AnalysisResultTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Shown.Add(table);
    }
}
