using YAT.app.Graphs.Export;

namespace YAT.App.Tests.TestDoubles;

// Scripted export dialogs. A path answers the save dialog; null is the user cancelling it.
internal sealed class FakeGraphExportDialogs : IGraphExportDialogs
{
    public string? PngPath { get; set; }

    public string? PowerPointPath { get; set; }

    public List<string> SuggestedNames { get; } = [];

    public List<string> Errors { get; } = [];

    public Task<string?> PickPngAsync(string suggestedFileName)
    {
        SuggestedNames.Add(suggestedFileName);
        return Task.FromResult(PngPath);
    }

    public Task<string?> PickPowerPointAsync(string suggestedFileName)
    {
        SuggestedNames.Add(suggestedFileName);
        return Task.FromResult(PowerPointPath);
    }

    public Task ShowErrorAsync(string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }
}
