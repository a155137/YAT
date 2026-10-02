using YAT.app.Composition;
using YAT.app.Updates;
using YAT.app.ViewModels;
using YAT.app.Views;
using static YAT.App.Tests.GraphPaletteLibraryDependencyTests;

namespace YAT.App.Tests;

// Architecture guard (Task #051.B): the update window and its view model know the update service and the launcher only.
// HTTP - the HttpClient, its handler, the update connection - is Infrastructure's, made once by the composition root; no
// view, view model or update controller holds or names it. And nothing that draws, presents or exports a graph knows
// that updates exist.
public class UpdateDependencyTests
{
    private static readonly string[] HttpNamespaces = ["System.Net.Http", "YAT.Infrastructure"];

    private static readonly string[] UpdateNamespaces = ["YAT.Application.Updates", "YAT.app.Updates", "YAT.Infrastructure.Updates", "System.Net.Http"];

    // Every view, view model and the update controller's namespace: the user interface of YAT.
    private static IEnumerable<Type> InterfaceTypes() =>
        typeof(UpdateViewModel).Assembly.GetTypes()
            .Where(type => type.Namespace is "YAT.app.ViewModels" or "YAT.app.Views" or "YAT.app.Updates");

    private static List<string> Offenders(IEnumerable<Type> types, string[] namespaces) =>
        types.SelectMany(type => ReferencedBy(type).Where(used => used is not null).SelectMany(Expand)
                .Where(used => namespaces.Any(prefix => (used.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)))
                .Select(used => $"{type.FullName} -> {used.FullName}"))
            .Distinct()
            .ToList();

    [Fact]
    public void NoViewOrViewModelOwnsHttpOrInfrastructure()
    {
        Assert.Empty(Offenders(InterfaceTypes(), HttpNamespaces));
    }

    [Fact]
    public void NothingThatDrawsPresentsOrExportsAGraphKnowsUpdates()
    {
        Assert.Empty(Offenders(DrawingTypes().Append(typeof(GraphWindow)), UpdateNamespaces));
    }

    [Fact]
    public void TheGuardSeesWhatItLooksFor()
    {
        Assert.Contains(typeof(UpdateWindow), InterfaceTypes());
        Assert.Contains(typeof(UpdateCheckController), InterfaceTypes());
        Assert.Contains(typeof(AvaloniaUpdateDialogs), InterfaceTypes());

        // The composition root is where HTTP is made, and the scan sees it there.
        Assert.NotEmpty(Offenders([typeof(CompositionRoot)], HttpNamespaces));
        Assert.Contains(typeof(System.Net.Http.HttpMessageHandler), ReferencedBy(typeof(CompositionRoot)).SelectMany(Expand));
    }
}
