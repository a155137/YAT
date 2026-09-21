using Avalonia.Controls;
using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// Analysis setup dialogs for the desktop window: the shared modal setup window, and the lifecycle message window for
// errors.
public sealed class AvaloniaAnalysisSetupDialogs : IAnalysisSetupDialogs
{
    private readonly Window _owner;

    public AvaloniaAnalysisSetupDialogs(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<AnalysisConfiguration?> ShowSetupAsync(AnalysisSetupViewModel setup) => AnalysisSetupWindow.ShowAsync(_owner, setup);

    public Task ShowErrorAsync(string message) => LifecycleMessageWindow.ShowErrorAsync(_owner, message);
}
