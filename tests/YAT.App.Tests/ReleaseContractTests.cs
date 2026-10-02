using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using YAT.app;
using YAT.Application.Distribution;

namespace YAT.App.Tests;

// The release contract (Task #051): Directory.Build.props' <Version> is a stable Major.Minor.Patch and the one version
// everything is written from - the assemblies, YAT.exe, the About dialog, the release's names - and nothing else carries
// a version of its own (app.manifest no longer does). build/publish.ps1 makes and verifies the release with the release
// rules themselves, keeps user settings out, and names no host: where a release is published is given when it is made.
public class ReleaseContractTests
{
    private static readonly Assembly Application = typeof(ApplicationInfo).Assembly;

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

    private static ReleaseVersion Written =>
        ReleaseVersion.Parse(Assert.Single(XDocument.Load(Path.Combine(Root, "Directory.Build.props")).Descendants("Version")).Value);

    private static string Publish => File.ReadAllText(Path.Combine(Root, "build", "publish.ps1"));

    [Fact]
    public void TheProductVersionIsAStableReleaseVersion()
    {
        var props = XDocument.Load(Path.Combine(Root, "Directory.Build.props"));

        Assert.True(ReleaseVersion.TryParse(Assert.Single(props.Descendants("Version")).Value, out _));
        Assert.Empty(props.Descendants("VersionPrefix"));
        Assert.Empty(props.Descendants("VersionSuffix"));
        Assert.Empty(props.Descendants("AssemblyVersion"));
        Assert.Empty(props.Descendants("FileVersion"));
    }

    [Fact]
    public void EveryVersionOfTheApplicationIsTheProductVersion()
    {
        var version = Written;
        var executable = Path.Combine(Path.GetDirectoryName(Application.Location)!, "YAT.exe");

        Assert.Equal(version.FileVersion, Application.GetName().Version!.ToString());
        Assert.Equal(version.FileVersion, Application.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Matches($"^{Regex.Escape(version.ToString())}(\\+|$)", Application.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.Equal(version.ToString(), ApplicationInfo.Current.Version);
        Assert.Equal(version.FileVersion, System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileVersion);
    }

    [Fact]
    public void TheReleaseIsNamedAfterTheProductVersion()
    {
        var version = Written;

        Assert.Equal($"YAT-v{version}-win-x64", version.PackageName("win-x64"));
        Assert.Equal($"v{version}", version.Tag);
        Assert.Contains("$name = \"YAT-v$version-$runtime\"", Publish, StringComparison.Ordinal);
        Assert.Contains("$zip = Join-Path $artifacts \"$name.zip\"", Publish, StringComparison.Ordinal);
        Assert.Contains("$tag = \"v$version\"", Publish, StringComparison.Ordinal);
    }

    // The Windows application manifest carries no version: an application manifest needs no assemblyIdentity, and one
    // with a version could only fall behind the product version.
    [Fact]
    public void TheApplicationManifestCarriesNoVersion()
    {
        var manifest = XDocument.Load(Path.Combine(Root, "src", "YAT.App", "app.manifest"));

        Assert.DoesNotContain(manifest.Descendants(), element => element.Name.LocalName == "assemblyIdentity");
        Assert.DoesNotContain(manifest.Descendants().Attributes(), attribute => attribute.Name.LocalName == "version");
        Assert.Contains(manifest.Descendants(), element => element.Name.LocalName == "supportedOS");
    }

    [Fact]
    public void PublishingMakesAndVerifiesTheReleaseWithTheReleaseRules()
    {
        var publish = Publish;

        Assert.True(File.Exists(Path.Combine(Root, "build", "YatRelease.cs")));
        Assert.StartsWith("#:project ../src/YAT.Infrastructure/YAT.Infrastructure.csproj", File.ReadAllText(Path.Combine(Root, "build", "YatRelease.cs")), StringComparison.Ordinal);
        foreach (var step in new[] { "@('version', $version)", "@('inventory', $release, $version, $runtime)", "@('package', $zip, $version, $runtime)", "@('verify', $artifacts, $version, $runtime)" })
        {
            Assert.Contains(step, publish, StringComparison.Ordinal);
        }

        // The inventory is in the release before it is zipped; the set is verified last.
        Assert.True(publish.IndexOf("'inventory'", StringComparison.Ordinal) < publish.IndexOf("ZipFile]::Open", StringComparison.Ordinal));
        Assert.True(publish.IndexOf("ZipFile]::Open", StringComparison.Ordinal) < publish.IndexOf("@('package', $zip", StringComparison.Ordinal));
        Assert.True(publish.IndexOf("@('package', $zip", StringComparison.Ordinal) < publish.IndexOf("@('verify'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOfficialReleaseNeedsACleanTreeAUrlAndItsOwnTag()
    {
        var publish = Publish;

        Assert.Contains("[switch]$Official", publish, StringComparison.Ordinal);
        Assert.Contains("An official release needs -PackageBaseUrl", publish, StringComparison.Ordinal);
        Assert.Contains("@('status', '--porcelain')", publish, StringComparison.Ordinal);
        Assert.Contains("already exists for another commit", publish, StringComparison.Ordinal);
        Assert.Contains("--require-manifest", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void UserSettingsNeverGoIntoARelease()
    {
        Assert.Contains("graph-palettes*.json", Publish, StringComparison.Ordinal);
        Assert.True(YAT.Infrastructure.Distribution.ReleaseArtifacts.IsUserSettingsFile("graph-palettes.json"));
        Assert.True(YAT.Infrastructure.Distribution.ReleaseArtifacts.IsUserSettingsFile("graph-palettes.json.1a2b.tmp"));
        Assert.True(YAT.Infrastructure.Distribution.ReleaseArtifacts.IsUserSettingsFile("graph-palettes.corrupt-20261002-093015.json"));
        Assert.False(YAT.Infrastructure.Distribution.ReleaseArtifacts.IsUserSettingsFile("YAT.deps.json"));
    }

    // Where a release is published is given when it is made (-PackageBaseUrl), never assumed: the release script names
    // only the documented placeholder. The one address YAT reads its update information from (Task #051.B) is written
    // once, as Directory.Build.props' YatUpdateManifestUrl, and reaches the application as assembly metadata - no source,
    // project or view writes a release host or manifest address of its own.
    [Fact]
    public void NoReleaseHostIsWrittenIn()
    {
        const string Endpoint = "https://github.com/a155137/YAT/releases/latest/download/yat-update.json";

        foreach (var url in Regex.Matches(Publish, "https://[^\\s'\"]+").Select(match => match.Value))
        {
            Assert.Contains("<owner>", url, StringComparison.Ordinal);
        }

        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        Assert.Equal(Endpoint, Assert.Single(XDocument.Parse(props).Descendants("YatUpdateManifestUrl")).Value);
        Assert.Single(Regex.Matches(props, "releases/latest"));
        Assert.Single(Regex.Matches(props, "a155137"));

        bool Written(string path) =>
            !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

        var files = new[] { "*.cs", "*.csproj", "*.axaml", "*.props", "*.targets", "*.json", "*.manifest", "*.ps1" }
            .SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(Root, "src"), pattern, SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(Root, "build"), pattern, SearchOption.AllDirectories)))
            .Concat(Directory.EnumerateFiles(Root, "*.json"))
            .Where(Written)
            .ToList();
        Assert.Contains(files, path => path.EndsWith("UpdateCheckController.cs", StringComparison.Ordinal));
        Assert.Contains(files, path => path.EndsWith("YAT.App.csproj", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("releases/latest", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("a155137", text, StringComparison.OrdinalIgnoreCase);
            if (Path.GetExtension(file) == ".cs")
            {
                Assert.DoesNotContain("github.com", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // YAT.App carries the property, not an address of its own; the built application has exactly that address.
        Assert.Contains("Value=\"$(YatUpdateManifestUrl)\"", File.ReadAllText(Path.Combine(Root, "src", "YAT.App", "YAT.App.csproj")), StringComparison.Ordinal);
        Assert.Equal(
            Endpoint,
            Application.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == YAT.app.Updates.UpdateEndpoint.MetadataKey).Value);
    }
}
