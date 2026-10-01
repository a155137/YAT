using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.ViewModels;

namespace YAT.App.Tests.TestDoubles;

// Scripted graph setup dialogs. Answer decides what the user does with the setup that is shown: it returns the
// configuration to confirm, or null to cancel; Layout is how several variables are to be drawn.
internal sealed class FakeGraphSetupDialogs : IGraphSetupDialogs
{
    public Func<GraphSetupViewModel, GraphConfiguration?> Answer { get; set; } = _ => null;

    // When set, used instead of Answer: what the user does with the setup, waiting for what the setup reads on the way
    // (the Filter dialog's values, Task #049).
    public Func<GraphSetupViewModel, Task<GraphConfiguration?>>? AnswerAsync { get; set; }

    public GraphVariableLayout Layout { get; set; } = GraphVariableLayout.Together;

    public List<GraphSetupViewModel> Shown { get; } = [];

    public List<string> Errors { get; } = [];

    public GraphSetupViewModel LastSetup => Shown.Count > 0 ? Shown[^1] : throw new InvalidOperationException("No graph setup was shown.");

    public async Task<GraphSetupRequest?> ShowSetupAsync(GraphSetupViewModel setup)
    {
        Shown.Add(setup);
        var configuration = AnswerAsync is not null ? await AnswerAsync(setup) : Answer(setup);
        return configuration is not null ? new GraphSetupRequest(configuration, Layout) : null;
    }

    public Task ShowErrorAsync(string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }
}
