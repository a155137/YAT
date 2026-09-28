using System.Xml.Linq;
using YAT.Application.Abstractions.Persistence;

namespace YAT.Application.Tests;

// Architecture guard: Application depends only on Domain (no Infrastructure, App, Analytics, UI or database frameworks).
public class ApplicationDependencyTests
{
    // The application project is YAT.App; its assembly is YAT (Task #048).
    private static readonly string[] ForbiddenAssemblies = ["YAT.Infrastructure", "YAT.App", "YAT", "YAT.Analytics"];

    private static readonly string[] ForbiddenAssemblyPrefixes = ["Avalonia", "DuckDB", "SkiaSharp", "HarfBuzzSharp", "Apache.Arrow", "CommunityToolkit"];

    [Fact]
    public void ApplicationAssemblyDoesNotReferenceOuterLayersOrFrameworks()
    {
        var referencedNames = typeof(IWorksheetRawDataStore).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();

        Assert.DoesNotContain(referencedNames, name => ForbiddenAssemblies.Contains(name));
        Assert.DoesNotContain(referencedNames, name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ApplicationProjectReferencesOnlyDomainAndNoPackages()
    {
        var project = XDocument.Load(ProjectFile("src/YAT.Application/YAT.Application.csproj"));

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
