using Avalonia.Controls;
using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// Capability analysis setup dialogs for the desktop window: the modal capability setup window, and the lifecycle
// message window for errors.
public sealed class AvaloniaCapabilityAnalysisSetupDialogs : ICapabilityAnalysisSetupDialogs
{
    private readonly Window _owner;

    public AvaloniaCapabilityAnalysisSetupDialogs(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<CapabilityAnalysisConfiguration?> ShowSetupAsync(CapabilityAnalysisSetupViewModel setup) =>
        CapabilityAnalysisSetupWindow.ShowAsync(_owner, setup);

    public Task ShowErrorAsync(string message) => LifecycleMessageWindow.ShowErrorAsync(_owner, message);
}
