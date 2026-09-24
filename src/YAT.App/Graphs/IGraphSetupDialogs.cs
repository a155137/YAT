using YAT.Application.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Graphs;

// The user interaction graph setup needs. The desktop implementation shows an Avalonia window; tests use a fake.
public interface IGraphSetupDialogs
{
    // Shows the graph setup for the prepared roles and columns. Returns what the user confirmed - the configuration,
    // and whether several variables are drawn together or separately - or null when they cancel.
    Task<GraphSetupRequest?> ShowSetupAsync(GraphSetupViewModel setup);

    Task ShowErrorAsync(string message);
}
