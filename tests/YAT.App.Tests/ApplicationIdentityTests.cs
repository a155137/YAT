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

    // ---- The YAT icon (Task #048.1) ----

    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static byte[] Icon() => File.ReadAllBytes(RepositoryFile("src/YAT.App/Assets/yat.ico"));

    // Each frame of the .ico: its size and where its image data lies.
    private static List<(int Size, int Offset, int Length)> Frames(byte[] icon)
    {
        Assert.Equal(0, BitConverter.ToUInt16(icon, 0));
        Assert.Equal(1, BitConverter.ToUInt16(icon, 2));
        var frames = new List<(int, int, int)>();
        for (var i = 0; i < BitConverter.ToUInt16(icon, 4); i++)
        {
            var entry = 6 + (16 * i);
            var size = icon[entry] == 0 ? 256 : icon[entry];
            Assert.Equal(icon[entry], icon[entry + 1]);
            Assert.Equal(32, BitConverter.ToUInt16(icon, entry + 6));
            frames.Add((size, BitConverter.ToInt32(icon, entry + 12), BitConverter.ToInt32(icon, entry + 8)));
        }

        return frames;
    }

    // The one icon the executable and every window take, and the logo beside it - and nothing standing in for them.
    [Fact]
    public void TheExecutableAndTheWindowsTakeTheYatIcon()
    {
        var project = File.ReadAllText(RepositoryFile("src/YAT.App/YAT.App.csproj"));
        var assets = Directory.GetFiles(RepositoryFile("src/YAT.App/Assets")).Select(Path.GetFileName).Order().ToList();

        Assert.Contains(@"<ApplicationIcon>Assets\yat.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Exists(", project, StringComparison.Ordinal);
        Assert.Equal(["yat-logo.png", "yat.ico"], assets);
        Assert.Equal("avares://YAT/Assets/yat.ico", AppIcon.YatIcon);
        Assert.Equal("avares://YAT/Assets/yat-logo.png", AppIcon.YatLogo);
        Assert.Equal(
            ["YatIcon", "YatLogo"],
            typeof(AppIcon).GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => field.Name).Order());
    }

    // Every size Windows asks for: 256 PNG-compressed, the smaller ones 32-bit images with their mask, as the shell and
    // older code alike read them.
    [Fact]
    public void TheIconHasEverySizeWindowsUses()
    {
        var icon = Icon();
        var frames = Frames(icon);

        Assert.Equal(IconSizes, frames.Select(frame => frame.Size));
        foreach (var (size, offset, length) in frames)
        {
            Assert.True(offset + length <= icon.Length);
            if (size == 256)
            {
                Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], icon[offset..(offset + 4)]);
            }
            else
            {
                Assert.Equal(40, BitConverter.ToInt32(icon, offset));
                Assert.Equal(size, BitConverter.ToInt32(icon, offset + 4));
                Assert.Equal(size * 2, BitConverter.ToInt32(icon, offset + 8));
                Assert.Equal(32, BitConverter.ToUInt16(icon, offset + 14));
                Assert.Equal(40 + (size * size * 4) + ((((size + 31) / 32) * 4) * size), length);
            }
        }
    }

    [Fact]
    public void TheLogoIsTheMarkAtTwiceItsSizeInTheAboutDialog()
    {
        var logo = File.ReadAllBytes(RepositoryFile("src/YAT.App/Assets/yat-logo.png"));
        var drawn = (int)(2 * YAT.app.Views.AboutWindow.LogoSize);

        Assert.Equal(96, YAT.app.Views.AboutWindow.LogoSize);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], logo[..4]);
        Assert.Equal(drawn, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(logo.AsSpan(16)));
        Assert.Equal(drawn, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(logo.AsSpan(20)));
    }

    // The built executable carries the icon: every frame's image, byte for byte, is among its resources.
    [Fact]
    public void TheExecutableCarriesTheIcon()
    {
        var executable = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Application.Location)!, "YAT.exe"));
        var icon = Icon();

        foreach (var (size, offset, length) in Frames(icon))
        {
            Assert.True(executable.AsSpan().IndexOf(icon.AsSpan(offset, length)) >= 0, $"The {size} x {size} frame is not in YAT.exe.");
        }
    }
}
