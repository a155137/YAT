using YAT.Application.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Graphs;

// The user interaction graph setup needs. The desktop implementation shows an Avalonia window; tests use a fake.
public interface IGraphSetupDialogs
{
    // Shows the graph setup for the prepared roles and columns. Returns the configuration the user confirmed, or null
    // when they cancel.
    Task<GraphConfiguration?> ShowSetupAsync(GraphSetupViewModel setup);

    Task ShowErrorAsync(string message);
}
