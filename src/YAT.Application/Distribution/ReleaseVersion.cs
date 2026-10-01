using System.Globalization;

namespace YAT.Application.Distribution;

// A YAT release's version (Task #051): stable Major.Minor.Patch, the form of Directory.Build.props' <Version> - the one
// product version every release name, file version, tag and manifest is written from. Each part is a whole number
// without a leading zero (0 itself is fine), as in Semantic Versioning; a prerelease or build suffix is not a stable
// version and is refused here.
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 3 })
        {
            return false;
        }

        var numbers = new int[3];
        for (var index = 0; index < 3; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || part.Length > 9 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            numbers[index] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a stable release version (Major.Minor.Patch).");

    // "0.2.0".
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    // The Windows file and assembly version of this release: "0.2.0.0".
    public string FileVersion => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}.0");

    // The release's tag: "v0.2.0".
    public string Tag => $"v{this}";

    // The release package of a runtime: "YAT-v0.2.0-win-x64".
    public string PackageName(string rid) => $"YAT-v{this}-{rid}";

    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
}
