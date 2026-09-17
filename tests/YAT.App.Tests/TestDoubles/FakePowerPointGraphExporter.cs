using YAT.app.Graphs.Export;

namespace YAT.App.Tests.TestDoubles;

// Records the presentations that would have been written, so the export workflow can be tested without a package.
internal sealed class FakePowerPointGraphExporter : IPowerPointGraphExporter
{
    public List<(string Path, PowerPointSlideImage Image)> Saved { get; } = [];

    public Exception? Failure { get; set; }

    public (string Path, PowerPointSlideImage Image) Last =>
        Saved.Count > 0 ? Saved[^1] : throw new InvalidOperationException("No presentation was saved.");

    public void Save(string path, PowerPointSlideImage image)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Saved.Add((path, image));
    }
}
