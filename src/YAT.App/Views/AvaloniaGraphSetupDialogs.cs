using Avalonia.Controls;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// Graph setup dialogs for the desktop window: the shared modal setup window, and the lifecycle message window for errors.
public sealed class AvaloniaGraphSetupDialogs : IGraphSetupDialogs
{
    private readonly Window _owner;

    public AvaloniaGraphSetupDialogs(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<GraphSetupRequest?> ShowSetupAsync(GraphSetupViewModel setup) =>
        GraphSetupWindow.ShowAsync(_owner, setup);

    public Task ShowErrorAsync(string message) => LifecycleMessageWindow.ShowErrorAsync(_owner, message);
}
