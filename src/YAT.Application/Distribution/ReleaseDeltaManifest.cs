using System.Text.Json;

namespace YAT.Application.Distribution;

// Metadata inside a differential release package (yat-delta.json): identifies the exact base release and target
// release, the SHA-256 of the base release's yat-files.json, and the exact sets of changed/added and removed files.
public sealed class ReleaseDeltaManifest
{
    public const int CurrentSchemaVersion = 1;

    public const string FileName = "yat-delta.json";

    public ReleaseDeltaManifest(
        ReleaseVersion fromVersion,
        ReleaseVersion toVersion,
        string rid,
        string baseInventorySha256,
        IEnumerable<string> changedFiles,
        IEnumerable<string> removedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        if (rid != rid.Trim())
        {
            throw new ArgumentException("The runtime id must not have leading or trailing whitespace.", nameof(rid));
        }

        if (toVersion <= fromVersion)
        {
            throw new ArgumentException($"Target version {toVersion} must be newer than base version {fromVersion}.", nameof(toVersion));
        }

        if (ReleaseManifestReader.NormalizeSha256(baseInventorySha256) != baseInventorySha256)
        {
            throw new ArgumentException("baseInventorySha256 must be 64 lower-case hex digits.", nameof(baseInventorySha256));
        }

        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(removedFiles);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = NormalizeAndValidatePaths(changedFiles, seen, nameof(changedFiles));
        var removed = NormalizeAndValidatePaths(removedFiles, seen, nameof(removedFiles));

        FromVersion = fromVersion;
        ToVersion = toVersion;
        Rid = rid;
        BaseInventorySha256 = baseInventorySha256;
        ChangedFiles = changed;
        RemovedFiles = removed;
    }

    public ReleaseVersion FromVersion { get; }

    public ReleaseVersion ToVersion { get; }

    public string Rid { get; }

    public string BaseInventorySha256 { get; }

    public IReadOnlyList<string> ChangedFiles { get; }

    public IReadOnlyList<string> RemovedFiles { get; }

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("fromVersion", FromVersion.ToString());
            writer.WriteString("toVersion", ToVersion.ToString());
            writer.WriteString("rid", Rid);
            writer.WriteString("baseInventorySha256", BaseInventorySha256);

            writer.WriteStartArray("changedFiles");
            foreach (var path in ChangedFiles)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("removedFiles");
            foreach (var path in RemovedFiles)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static bool TryRead(string json, out ReleaseDeltaManifest? delta, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(json);
        delta = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            problem = $"The delta manifest is not JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "The delta manifest is not a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                problem = $"The delta manifest schemaVersion must be {CurrentSchemaVersion}.";
                return false;
            }

            if (!root.TryGetProperty("fromVersion", out var fromEl) ||
                fromEl.ValueKind != JsonValueKind.String ||
                !ReleaseVersion.TryParse(fromEl.GetString(), out var fromVersion))
            {
                problem = "The delta manifest has no valid fromVersion.";
                return false;
            }

            if (!root.TryGetProperty("toVersion", out var toEl) ||
                toEl.ValueKind != JsonValueKind.String ||
                !ReleaseVersion.TryParse(toEl.GetString(), out var toVersion))
            {
                problem = "The delta manifest has no valid toVersion.";
                return false;
            }

            if (toVersion <= fromVersion)
            {
                problem = $"Target version {toVersion} must be newer than base version {fromVersion}.";
                return false;
            }

            if (!root.TryGetProperty("rid", out var ridEl) ||
                ridEl.ValueKind != JsonValueKind.String ||
                ridEl.GetString() is not { } rid ||
                string.IsNullOrWhiteSpace(rid) ||
                rid != rid.Trim())
            {
                problem = "The delta manifest has no valid rid.";
                return false;
            }

            if (!root.TryGetProperty("baseInventorySha256", out var shaEl) ||
                shaEl.ValueKind != JsonValueKind.String ||
                ReleaseManifestReader.NormalizeSha256(shaEl.GetString()) is not { } baseSha ||
                baseSha != shaEl.GetString())
            {
                problem = "The delta manifest has no valid lower-case baseInventorySha256.";
                return false;
            }

            if (!TryReadPathArray(root, "changedFiles", out var changed, out problem) ||
                !TryReadPathArray(root, "removedFiles", out var removed, out problem))
            {
                return false;
            }

            try
            {
                delta = new ReleaseDeltaManifest(fromVersion, toVersion, rid, baseSha, changed!, removed!);
                problem = null;
                return true;
            }
            catch (ArgumentException exception)
            {
                problem = exception.Message;
                return false;
            }
        }
    }

    private static IReadOnlyList<string> NormalizeAndValidatePaths(
        IEnumerable<string> paths,
        HashSet<string> seen,
        string paramName)
    {
        var list = new List<string>();
        foreach (var raw in paths)
        {
            if (raw is null || ReleasePath.Normalize(raw) is not { } path || path != raw)
            {
                throw new ArgumentException($"'{raw}' is not a valid release path.", paramName);
            }

            if (string.Equals(path, ReleaseInventory.FileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The delta manifest cannot list '{path}'.", paramName);
            }

            if (ReleaseInstallation.IsReserved(path))
            {
                throw new ArgumentException($"'{path}' is inside the reserved updater folder.", paramName);
            }

            if (ReleaseInstallation.IsUserData(path))
            {
                throw new ArgumentException($"'{path}' is inside the protected '{ReleaseInstallation.UserDataFolderName}/' directory.", paramName);
            }

            var fileName = path.Split('/')[^1];
            if (ReleasePackageVerifier.IsUserSettingsFile(fileName))
            {
                throw new ArgumentException($"'{path}' is a user settings file.", paramName);
            }

            if (!seen.Add(path))
            {
                throw new ArgumentException($"'{path}' is listed more than once or in both changedFiles and removedFiles.", paramName);
            }

            list.Add(path);
        }

        return [.. list.OrderBy(p => p, StringComparer.Ordinal)];
    }

    private static bool TryReadPathArray(
        JsonElement root,
        string propertyName,
        out List<string>? paths,
        out string? problem)
    {
        paths = null;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            problem = $"The delta manifest has no {propertyName} array.";
            return false;
        }

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value)
            {
                problem = $"The delta manifest {propertyName} contains a non-string entry.";
                return false;
            }

            list.Add(value);
        }

        paths = list;
        problem = null;
        return true;
    }
}
