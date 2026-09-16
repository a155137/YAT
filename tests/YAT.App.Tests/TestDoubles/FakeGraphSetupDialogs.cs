using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.ViewModels;

namespace YAT.App.Tests.TestDoubles;

// Scripted graph setup dialogs. Answer decides what the user does with the setup that is shown: it returns the
// configuration to confirm, or null to cancel.
internal sealed class FakeGraphSetupDialogs : IGraphSetupDialogs
{
    public Func<GraphSetupViewModel, GraphConfiguration?> Answer { get; set; } = _ => null;

    public List<GraphSetupViewModel> Shown { get; } = [];

    public List<string> Errors { get; } = [];

    public GraphSetupViewModel LastSetup => Shown.Count > 0 ? Shown[^1] : throw new InvalidOperationException("No graph setup was shown.");

    public Task<GraphConfiguration?> ShowSetupAsync(GraphSetupViewModel setup)
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
