using System.Reflection;

namespace YAT.app;

// What the application says about itself (Task #048): its name, what the name stands for, its version, who publishes it
// and their copyright - all read from the assembly's metadata, which Directory.Build.props sets in one place. Nothing
// here, and nothing that shows it, writes a version of its own.
public sealed record ApplicationInfo(string Product, string Description, string Version, string Company, string Copyright)
{
    // The running application's.
    public static ApplicationInfo Current { get; } = From(typeof(ApplicationInfo).Assembly);

    public static ApplicationInfo From(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new ApplicationInfo(
            assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? assembly.GetName().Name ?? string.Empty,
            assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? string.Empty,
            DisplayVersion(informational ?? assembly.GetName().Version?.ToString(3) ?? string.Empty),
            assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? string.Empty,
            assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty);
    }

    // The version as people read it: "0.1.0+516ada6..." is "0.1.0" - the commit the build came from is not shown.
    public static string DisplayVersion(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);

        var metadata = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informationalVersion : informationalVersion[..metadata];
    }
}
