using System.Xml.Linq;

namespace YAT.Updater.Tests;

// Architecture guard (Task #051.C): the updater knows the release contracts of YAT.Application (and so Domain) and the
// BCL - no UI, no Infrastructure, no database, no drawing, no network, no packages.
public class UpdaterDependencyTests
{
    private static readonly string[] ForbiddenAssemblies = ["YAT", "YAT.Infrastructure", "YAT.Analytics"];

    private static readonly string[] ForbiddenPrefixes = ["Avalonia", "DuckDB", "SkiaSharp", "HarfBuzzSharp", "CommunityToolkit", "DocumentFormat", "System.Net.Http"];

    [Fact]
    public void TheUpdaterReferencesOnlyTheApplicationLayerAndTheBcl()
    {
        var referenced = typeof(Installer).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();

        Assert.Contains("YAT.Application", referenced);
        Assert.DoesNotContain(referenced, name => ForbiddenAssemblies.Contains(name));
        Assert.DoesNotContain(referenced, name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TheUpdaterProjectReferencesOnlyTheApplicationAndNoPackages()
    {
        var project = XDocument.Load(Path.Combine(Root, "src", "YAT.Updater", "YAT.Updater.csproj"));

        Assert.Equal(
            ["YAT.Application"],
            project.Descendants("ProjectReference").Select(reference => Path.GetFileNameWithoutExtension(((string)reference.Attribute("Include")!).Replace('\\', '/'))));
        Assert.Empty(project.Descendants("PackageReference"));
    }

    private static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "YAT.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException("Repository root containing YAT.slnx was not found.");
        }
    }
}
