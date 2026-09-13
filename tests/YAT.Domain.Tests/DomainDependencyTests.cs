using System.Xml.Linq;
using YAT.Domain.Entities;

namespace YAT.Domain.Tests;

// Architecture guard: Domain depends on nothing (no projects, no packages, no UI/database frameworks).
public class DomainDependencyTests
{
    private static readonly string[] ForbiddenAssemblies = ["YAT.Application", "YAT.Infrastructure", "YAT.App", "YAT.Analytics"];

    private static readonly string[] ForbiddenAssemblyPrefixes = ["Avalonia", "DuckDB", "SkiaSharp", "HarfBuzzSharp", "Apache.Arrow", "CommunityToolkit"];

    [Fact]
    public void DomainAssemblyDoesNotReferenceOtherLayersOrFrameworks()
    {
        var referencedNames = typeof(Worksheet).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();

        Assert.DoesNotContain(referencedNames, name => ForbiddenAssemblies.Contains(name));
        Assert.DoesNotContain(referencedNames, name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DomainProjectDeclaresNoProjectOrPackageReferences()
    {
        var project = XDocument.Load(ProjectFile("src/YAT.Domain/YAT.Domain.csproj"));

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
    }

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
