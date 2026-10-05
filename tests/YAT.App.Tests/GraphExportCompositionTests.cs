using System.Reflection;
using YAT.app.Composition;
using YAT.app.Graphs.Export;
using YAT.app.Views;

namespace YAT.App.Tests;

// Where the export workflow is put together: in the composition and the presenter, never in the graph window. A window
// that built its own service, exporter and dialogs would be a composition root of its own.
public class GraphExportCompositionTests
{
    private static readonly Type GraphWindow =
        typeof(AvaloniaGraphWindowPresenter).Assembly.GetType("YAT.app.Views.GraphWindow")
        ?? throw new InvalidOperationException("GraphWindow was not found.");

    private static readonly Type[] DependenciesTheWindowMustNotBuild =
    [
        typeof(GraphExportService),
        typeof(PowerPointGraphExporter),
        typeof(AvaloniaGraphExportDialogs)
    ];

    // 1
    [Fact]
    public void AGraphWindowIsGivenItsExportWorkflowInsteadOfBuildingOne()
    {
        var constructor = Assert.Single(GraphWindow.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        Assert.Equal(
            // And, since Task #050, the user's palette choices for Edit Appearance... - never where they are kept - and, since
            // Task #058, the panels of a graph drawn in panels.
            ["GraphPresentationState", "IGraphPlotRenderer", "IGraphExportWorkflowFactory", "IGraphPaletteLibraryAccess", "IReadOnlyList`1"],
            constructor.GetParameters().Select(parameter => parameter.ParameterType.Name));
    }

    // 2
    [Fact]
    public void AGraphWindowHoldsNoExportDependencyOfItsOwn()
    {
        var fields = GraphWindow
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(fields, field => DependenciesTheWindowMustNotBuild.Contains(field));
        Assert.Contains(typeof(GraphExportController), fields);
    }

    // 3
    [Fact]
    public void ThePresenterCarriesTheFactoryToTheWindowsItOpens()
    {
        var constructor = Assert.Single(typeof(AvaloniaGraphWindowPresenter).GetConstructors());

        // And, since Task #061, the Graphs list it lists every window in.
        Assert.Equal(
            ["Window", "IGraphExportWorkflowFactory", "IGraphPaletteLibraryAccess", "OpenGraphsViewModel"],
            constructor.GetParameters().Select(parameter => parameter.ParameterType.Name));
    }

    // 4
    [Fact]
    public void TheCompositionRootOwnsTheWindowIndependentHalvesOfAnExport()
    {
        var composition = new CompositionRoot(TimeProvider.System);

        Assert.IsType<GraphExportService>(composition.CreateGraphExportService());
        Assert.IsType<PowerPointGraphExporter>(composition.CreatePowerPointExporter());
    }

    // 5
    [Fact]
    public void TheFactoryIsBuiltFromThoseHalvesAndNothingElse()
    {
        var composition = new CompositionRoot(TimeProvider.System);
        var factory = new AvaloniaGraphExportWorkflowFactory(composition.CreateGraphExportService(), composition.CreatePowerPointExporter());

        Assert.IsAssignableFrom<IGraphExportWorkflowFactory>(factory);
        Assert.Throws<ArgumentNullException>(() => new AvaloniaGraphExportWorkflowFactory(null!, composition.CreatePowerPointExporter()));
        Assert.Throws<ArgumentNullException>(() => new AvaloniaGraphExportWorkflowFactory(composition.CreateGraphExportService(), null!));
        Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
    }
}
