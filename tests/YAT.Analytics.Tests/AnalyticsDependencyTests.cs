using System.Reflection;
using System.Xml.Linq;

namespace YAT.Analytics.Tests;

// Architecture guard: Analytics depends only on Domain (no Application, Infrastructure, UI or database frameworks).
public class AnalyticsDependencyTests
{
    private static readonly string[] ForbiddenAssemblies = ["YAT.Application", "YAT.Infrastructure", "YAT.App"];

    private static readonly string[] ForbiddenAssemblyPrefixes = ["Avalonia", "DuckDB", "SkiaSharp", "HarfBuzzSharp", "Apache.Arrow", "CommunityToolkit"];

    [Fact]
    public void AnalyticsAssemblyDoesNotReferenceOtherLayersOrFrameworks()
    {
        var referencedNames = Assembly.Load(new AssemblyName("YAT.Analytics")).GetReferencedAssemblies().Select(name => name.Name!).ToArray();

        Assert.DoesNotContain(referencedNames, name => ForbiddenAssemblies.Contains(name));
        Assert.DoesNotContain(referencedNames, name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AnalyticsProjectReferencesOnlyDomainAndNoPackages()
    {
        var project = XDocument.Load(ProjectFile("src/YAT.Analytics/YAT.Analytics.csproj"));

        Assert.Equal(["YAT.Domain"], ProjectReferenceNames(project));
        Assert.Empty(project.Descendants("PackageReference"));
    }

    private static string[] ProjectReferenceNames(XDocument project) =>
        project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(((string)reference.Attribute("Include")!).Replace('\\', '/')))
            .ToArray();

    private static string ProjectFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "YAT.slnx")))
            {
                return Path.Combine(directory.FullName, relativePath);
            }
        }

        throw new InvalidOperationException("Repository root containing YAT.slnx was not found.");
    }
}
