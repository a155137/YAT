using Avalonia.Controls;
using YAT.app.Analyses;

namespace YAT.app.Views;

// Opens analysis results as windows of the desktop application. A result window is not modal: the user keeps working
// in the main window while it is open.
public sealed class AvaloniaAnalysisResultPresenter : IAnalysisResultPresenter
{
    private readonly Window _owner;

    public AvaloniaAnalysisResultPresenter(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public void ShowResult(AnalysisResultTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        new AnalysisResultWindow(table).Show(_owner);
    }
}
