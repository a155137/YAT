using System.Reflection;
using System.Xml.Linq;
using YAT.app;

namespace YAT.App.Tests;

// Task #048: what the application says about itself comes from its assembly's metadata, set once in
// Directory.Build.props - the About dialog shows it and writes no version of its own - and the executable is YAT.exe.
public class ApplicationIdentityTests
{
    private static readonly Assembly Application = typeof(ApplicationInfo).Assembly;

    private static string RepositoryFile(string relativePath)
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

    [Fact]
    public void TheApplicationAssemblyIsYat()
    {
        Assert.Equal("YAT", Application.GetName().Name);
        Assert.Equal("YAT", ApplicationInfo.Current.Product);
        Assert.Equal("YuXiang Analysis Tool", ApplicationInfo.Current.Description);
        Assert.Equal("YX Studio", ApplicationInfo.Current.Company);
        Assert.Equal("Copyright © 2026 YX Studio", ApplicationInfo.Current.Copyright);
    }

    // What Windows shows as the file description: the assembly title, in the executable's version resource too.
    [Fact]
    public void TheFileDescriptionIsYuXiangAnalysisTool()
    {
        Assert.Equal("YuXiang Analysis Tool", Application.GetCustomAttribute<AssemblyTitleAttribute>()!.Title);
        Assert.Equal("YuXiang Analysis Tool", Application.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description);

        var executable = Path.Combine(Path.GetDirectoryName(Application.Location)!, "YAT.exe");
        Assert.True(File.Exists(executable), $"{executable} was not built.");
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable);
        Assert.Equal("YuXiang Analysis Tool", version.FileDescription);
        Assert.Equal("YAT", version.ProductName);
        Assert.Equal("YX Studio", version.CompanyName);
        Assert.Equal("Copyright © 2026 YX Studio", version.LegalCopyright);
        Assert.Equal(Application.GetName().Version!.ToString(), version.FileVersion);
        Assert.StartsWith(Application.GetName().Version!.ToString(3), version.ProductVersion, StringComparison.Ordinal);
    }

    // One version: the assembly version, the file version and the informational version all say the same, and the
    // version shown is that one - whatever it is - without the commit.
    [Fact]
    public void TheVersionShownIsTheAssemblysVersion()
    {
        var version = Application.GetName().Version!;
        var file = Application.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
        var informational = Application.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(version.ToString(), file);
        Assert.StartsWith(version.ToString(3), informational, StringComparison.Ordinal);
        Assert.Equal(version.ToString(3), ApplicationInfo.Current.Version);
        Assert.Equal(ApplicationInfo.From(Application), ApplicationInfo.Current);
    }

    // Directory.Build.props is the only place the version is written, and every YAT assembly carries it.
    [Fact]
    public void TheVersionIsWrittenOnceForEveryAssembly()
    {
        var props = XDocument.Load(RepositoryFile("Directory.Build.props"));
        var written = Assert.Single(props.Descendants("Version")).Value;

        Assert.Equal(written, ApplicationInfo.Current.Version);
        foreach (var assembly in new[] { Application, typeof(YAT.Domain.Entities.Worksheet).Assembly, typeof(YAT.Application.Graphs.GraphConfiguration).Assembly })
        {
            Assert.Equal(written, assembly.GetName().Version!.ToString(3));
        }

        foreach (var project in Directory.GetFiles(RepositoryFile("src"), "*.csproj", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(project);
            Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName is "Version" or "AssemblyVersion" or "FileVersion" or "InformationalVersion");
        }
    }

    [Theory]
    [InlineData("0.1.0+516ada6406d468b60fb3bd75a590bee0d053e78d", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("1.2.3-beta.1+abc", "1.2.3-beta.1")]
    [InlineData("", "")]
    public void TheCommitIsNotPartOfTheVersionShown(string informational, string shown) =>
        Assert.Equal(shown, ApplicationInfo.DisplayVersion(informational));

    // The one YAT icon, once it is added, serves the executable and every window: the project and AppIcon look for it
    // in the same place. Until then the stand-in AppIcon falls back to is there.
    [Fact]
    public void TheExecutableAndTheWindowsTakeTheIconFromOnePlace()
    {
        var project = File.ReadAllText(RepositoryFile("src/YAT.App/YAT.App.csproj"));

        Assert.Contains(@"<ApplicationIcon>Assets\yat.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains(@"Exists('$(MSBuildProjectDirectory)\Assets\yat.ico')", project, StringComparison.Ordinal);
        Assert.Equal("avares://YAT/Assets/yat.ico", AppIcon.YatIcon);
        Assert.True(File.Exists(RepositoryFile("src/YAT.App/Assets/avalonia-logo.ico")));
        Assert.Equal("avares://YAT/Assets/avalonia-logo.ico", AppIcon.StandInIcon);
    }
}
