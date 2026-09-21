using YAT.Application.Analyses;
using YAT.app.ViewModels;

namespace YAT.app.Analyses;

// The user interaction a capability analysis setup needs. The desktop implementation shows an Avalonia window; tests
// use a fake.
//
// It is its own seam rather than the descriptive statistics one, because a capability setup returns a different
// configuration: variables with their own specification limits and the statistics to show.
public interface ICapabilityAnalysisSetupDialogs
{
    // Shows the capability setup for the prepared columns. Returns the configuration the user confirmed, or null when
    // they cancel.
    Task<CapabilityAnalysisConfiguration?> ShowSetupAsync(CapabilityAnalysisSetupViewModel setup);

    Task ShowErrorAsync(string message);
}
