using YAT.Application.Analyses;
using YAT.app.ViewModels;

namespace YAT.app.Analyses;

// The user interaction an analysis setup needs. The desktop implementation shows an Avalonia window; tests use a fake.
public interface IAnalysisSetupDialogs
{
    // Shows the analysis setup for the prepared columns. Returns the configuration the user confirmed, or null when
    // they cancel.
    Task<AnalysisConfiguration?> ShowSetupAsync(AnalysisSetupViewModel setup);

    Task ShowErrorAsync(string message);
}
