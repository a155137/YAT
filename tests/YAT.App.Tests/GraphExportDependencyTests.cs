using System.Reflection;
using YAT.app.Graphs.Export;

namespace YAT.App.Tests;

// Architecture guard: an export draws a prepared graph. Its public surface therefore knows about render models and
// plot renderers, and nothing about worksheets, graph data, sessions, repositories or DuckDB - which is what makes it
// impossible for exporting to reload or recompute anything.
public class GraphExportDependencyTests
{
    private static readonly Type[] ExportTypes =
    [
        typeof(GraphExportService),
        typeof(GraphExportSnapshot),
        typeof(GraphExportController),
        typeof(IGraphExportDialogs),
        typeof(IPowerPointGraphExporter),
        typeof(PowerPointGraphExporter),
        typeof(PowerPointSlideImage),
        typeof(ExportFileNames),
        typeof(SlideImageLayout)
    ];

    private static readonly string[] ForbiddenNamespaces =
    [
        "YAT.Application",
        "YAT.Infrastructure",
        "YAT.Domain",
        "YAT.app.Composition",
        "DuckDB"
    ];

    private static IEnumerable<Type> SurfaceOf(Type type)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(Public))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(Public))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var constructor in type.GetConstructors(Public))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    // 1
    [Fact]
    public void NoExportTypeMentionsGraphDataSessionsOrStorage()
    {
        foreach (var type in ExportTypes)
        {
            foreach (var used in SurfaceOf(type))
            {
                var name = used.FullName ?? used.Name;
                Assert.DoesNotContain(
                    ForbiddenNamespaces,
                    forbidden => name.StartsWith(forbidden + ".", StringComparison.Ordinal));
            }
        }
    }

    // 2
    [Fact]
    public void AnExportIsTakenFromARenderModelAndAPlotRenderer()
    {
        var constructor = Assert.Single(typeof(GraphExportSnapshot).GetConstructors());

        Assert.Equal(
            ["GraphRenderModel", "IGraphPlotRenderer", "GraphTheme"],
            constructor.GetParameters().Select(parameter => parameter.ParameterType.Name));
    }

    // 3
    [Fact]
    public void ThePresentationExporterIsGivenAnImageRatherThanAGraph()
    {
        // PowerPoint must not render a graph of its own: it receives the bytes the PNG export produced.
        var save = Assert.Single(typeof(IPowerPointGraphExporter).GetMethods());

        Assert.Equal(["String", "PowerPointSlideImage"], save.GetParameters().Select(parameter => parameter.ParameterType.Name));
        Assert.Equal(typeof(byte[]), typeof(PowerPointSlideImage).GetProperty(nameof(PowerPointSlideImage.Png))!.PropertyType);
    }
}
