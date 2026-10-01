using System.Text.Json;

namespace YAT.Application.Distribution;

// One file of a release: where it is in the release folder, its size, and its SHA-256 (64 hex digits, lower case).
public sealed record ReleaseFile(string Path, long Size, string Sha256);

// Every file a YAT release contains (Task #051), as yat-files.json at the root of the release folder - and so of its
// ZIP. It is what an update will be able to rely on: which files are the program's (so only those are ever replaced or
// removed, and anything else in the folder is left alone), and what each must be.
//
//     { "schemaVersion": 1, "version": "0.2.0", "rid": "win-x64",
//       "files": [ { "path": "YAT.exe", "size": 123, "sha256": "<64 hex>" }, ... ] }
//
// Paths are relative to the release folder, separated by '/', never absolute, never with "." or ".." parts, unique
// ignoring case (Windows), and listed in ordinal order - so the same release always gives the same inventory, byte for
// byte. The inventory does not list itself.
public sealed class ReleaseInventory
{
    public const int CurrentSchemaVersion = 1;

    public const string FileName = "yat-files.json";

    // Throws ArgumentException when a file breaks a rule.
    public ReleaseInventory(ReleaseVersion version, string rid, IEnumerable<ReleaseFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        ArgumentNullException.ThrowIfNull(files);

        var list = new List<ReleaseFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (ReleasePath.Normalize(file.Path) is not { } path || path != file.Path)
            {
                throw new ArgumentException($"'{file.Path}' is not a release path.", nameof(files));
            }

            if (string.Equals(path, FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The inventory does not list itself.", nameof(files));
            }

            if (!seen.Add(path))
            {
                throw new ArgumentException($"'{path}' is listed twice.", nameof(files));
            }

            if (file.Size < 0 || ReleaseManifestReader.NormalizeSha256(file.Sha256) != file.Sha256)
            {
                throw new ArgumentException($"'{path}' has no size or no lower-case SHA-256.", nameof(files));
            }

            list.Add(file);
        }

        Version = version;
        Rid = rid;
        Files = [.. list.OrderBy(file => file.Path, StringComparer.Ordinal)];
    }

    public ReleaseVersion Version { get; }

    public string Rid { get; }

    // In ordinal order of their paths.
    public IReadOnlyList<ReleaseFile> Files { get; }

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("version", Version.ToString());
            writer.WriteString("rid", Rid);
            writer.WriteStartArray("files");
            foreach (var file in Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteNumber("size", file.Size);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // The inventory in the text, or why it is not one. Never throws for what is in the text.
    public static bool TryRead(string json, out ReleaseInventory? inventory, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(json);
        inventory = null;
        problem = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != CurrentSchemaVersion)
            {
                problem = "The inventory is not schema version 1.";
                return false;
            }

            if (!root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String
                || !ReleaseVersion.TryParse(versionElement.GetString(), out var version))
            {
                problem = "The inventory has no stable version.";
                return false;
            }

            if (!root.TryGetProperty("rid", out var ridElement) || ridElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(ridElement.GetString()))
            {
                problem = "The inventory has no runtime id.";
                return false;
            }

            if (!root.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            {
                problem = "The inventory has no files.";
                return false;
            }

            var files = new List<ReleaseFile>();
            foreach (var element in filesElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                    || !element.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes)
                    || !element.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String)
                {
                    problem = "An inventory entry is not a file.";
                    return false;
                }

                files.Add(new ReleaseFile(path.GetString()!, bytes, sha.GetString()!));
            }

            inventory = new ReleaseInventory(version, ridElement.GetString()!, files);
            return true;
        }
        catch (JsonException exception)
        {
            problem = $"The inventory is not JSON: {exception.Message}";
            return false;
        }
        catch (ArgumentException exception)
        {
            problem = exception.Message;
            return false;
        }
    }
}

// A path inside a release folder, as an inventory writes it (Task #051).
public static class ReleasePath
{
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '|', '?', '*'];

    // The path relative to the release folder, with '/' separators - or null when it is not one: empty, rooted (a
    // drive, a leading separator, a UNC path), with an empty, "." or ".." part, or with a character Windows does not
    // allow in a name.
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var unified = path.Replace('\\', '/');
        if (unified.StartsWith('/') || unified.EndsWith('/') || unified.Any(character => character < ' ' || Forbidden.Contains(character)))
        {
            return null;
        }

        var parts = unified.Split('/');
        return parts.Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(' ') || part.EndsWith('.'))
            ? null
            : unified;
    }
}
