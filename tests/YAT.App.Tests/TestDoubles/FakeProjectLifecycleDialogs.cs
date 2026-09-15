using YAT.app.Lifecycle;

namespace YAT.App.Tests.TestDoubles;

// Scripted project lifecycle dialogs. Every picker result and prompt answer must be queued by the test: an unexpected
// dialog fails the test.
internal sealed class FakeProjectLifecycleDialogs : IProjectLifecycleDialogs
{
    public Queue<string?> OpenPaths { get; } = new();

    public Queue<string?> SavePaths { get; } = new();

    public Queue<SaveChangesChoice> Choices { get; } = new();

    public List<string> SuggestedFileNames { get; } = [];

    public List<(string ProjectName, bool Closing)> Prompts { get; } = [];

    public List<string> Errors { get; } = [];

    public Task<string?> PickProjectToOpenAsync() =>
        Task.FromResult(OpenPaths.Count > 0 ? OpenPaths.Dequeue() : throw new InvalidOperationException("Unexpected open picker."));

    public Task<string?> PickSaveLocationAsync(string suggestedFileName)
    {
        SuggestedFileNames.Add(suggestedFileName);
        return Task.FromResult(SavePaths.Count > 0 ? SavePaths.Dequeue() : throw new InvalidOperationException("Unexpected save picker."));
    }

    public Task<SaveChangesChoice> AskSaveChangesAsync(string projectName, bool closing)
    {
        Prompts.Add((projectName, closing));
        return Task.FromResult(Choices.Count > 0 ? Choices.Dequeue() : throw new InvalidOperationException("Unexpected save prompt."));
    }

    public Task ShowErrorAsync(string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }
}
