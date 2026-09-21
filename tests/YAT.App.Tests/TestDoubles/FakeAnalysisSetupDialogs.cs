using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.app.ViewModels;

namespace YAT.App.Tests.TestDoubles;

// Scripted analysis setup dialogs. Answer decides what the user does with the setup that is shown: it returns the
// configuration to confirm, or null to cancel.
internal sealed class FakeAnalysisSetupDialogs : IAnalysisSetupDialogs
{
    public Func<AnalysisSetupViewModel, AnalysisConfiguration?> Answer { get; set; } = _ => null;

    public List<AnalysisSetupViewModel> Shown { get; } = [];

    public List<string> Errors { get; } = [];

    public AnalysisSetupViewModel LastSetup =>
        Shown.Count > 0 ? Shown[^1] : throw new InvalidOperationException("No analysis setup was shown.");

    public Task<AnalysisConfiguration?> ShowSetupAsync(AnalysisSetupViewModel setup)
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
