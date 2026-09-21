using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.app.ViewModels;

namespace YAT.App.Tests.TestDoubles;

// Scripted capability analysis setup dialogs. Answer decides what the user does with the setup that is shown: it
// returns the configuration to confirm, or null to cancel.
internal sealed class FakeCapabilityAnalysisSetupDialogs : ICapabilityAnalysisSetupDialogs
{
    public Func<CapabilityAnalysisSetupViewModel, CapabilityAnalysisConfiguration?> Answer { get; set; } = _ => null;

    public List<CapabilityAnalysisSetupViewModel> Shown { get; } = [];

    public List<string> Errors { get; } = [];

    public CapabilityAnalysisSetupViewModel LastSetup =>
        Shown.Count > 0 ? Shown[^1] : throw new InvalidOperationException("No capability setup was shown.");

    public Task<CapabilityAnalysisConfiguration?> ShowSetupAsync(CapabilityAnalysisSetupViewModel setup)
    {
        Shown.Add(setup);
        return Task.FromResult(Answer(setup));
    }

    public Task ShowErrorAsync(string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }
}
