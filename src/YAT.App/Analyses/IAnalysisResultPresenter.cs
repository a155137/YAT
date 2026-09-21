namespace YAT.app.Analyses;

// How a finished analysis reaches the screen. The desktop implementation opens the shared analysis result window;
// tests use a fake.
//
// The seam takes the result table and nothing else - no configuration, worksheet data or analysis type - so every
// analysis that can describe itself as a table reaches the screen the same way, and the window never learns what
// produced it.
public interface IAnalysisResultPresenter
{
    void ShowResult(AnalysisResultTable table);
}
