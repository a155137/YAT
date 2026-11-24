using System.Text.Json;

namespace YAT.Application.Distribution;

// One release package of the manifest: a runtime's ZIP, where it is, and how to know it is the one released.
public sealed record ReleasePackage(string Rid, Uri Url, string Sha256, long Size);

// An optional differential package of the manifest: updates an adjacent baseVersion for a runtime to this release.
public sealed record ReleaseDeltaPackage(string Rid, ReleaseVersion BaseVersion, Uri Url, string Sha256, long Size);

// What a YAT release says about itself (Task #051): its version, where its packages are, and their SHA-256 and size -
// the yat-update.json published beside the packages. YAT's own format, whoever hosts it: nothing here knows GitHub.
//
//     { "schemaVersion": 1, "version": "0.2.0", "publishedAt": "<ISO 8601, optional>",
//       "releaseNotesUrl": "<https, optional>",
//       "packages": [ { "rid": "win-x64", "url": "https://...", "sha256": "<64 hex>", "size": 123 } ],
//       "deltas": [ { "rid": "win-x64", "baseVersion": "0.1.0", "url": "https://...", "sha256": "<64 hex>", "size": 45 } ] }
//
// Integrity, not authenticity: the SHA-256 tells that a package is the one the manifest names, not who published the
// manifest.
public sealed record ReleaseManifest(
    ReleaseVersion Version,
    IReadOnlyList<ReleasePackage> Packages,
    DateTimeOffset? PublishedAt = null,
    Uri? ReleaseNotesUrl = null,
    IReadOnlyList<ReleaseDeltaPackage>? Deltas = null)
{
    public const int CurrentSchemaVersion = 1;

    public const string FileName = "yat-update.json";

    public IReadOnlyList<ReleaseDeltaPackage> DeltaPackages => Deltas ?? [];

    // The package for a runtime, or null when the release has none.
    public ReleasePackage? PackageFor(string rid) =>
        Packages.FirstOrDefault(package => string.Equals(package.Rid, rid, StringComparison.OrdinalIgnoreCase));

    // The differential package for a runtime and installed base version, or null when none is offered.
    public ReleaseDeltaPackage? DeltaFor(string rid, ReleaseVersion baseVersion) =>
        DeltaPackages.FirstOrDefault(delta =>
            string.Equals(delta.Rid, rid, StringComparison.OrdinalIgnoreCase)
            && delta.BaseVersion == baseVersion);

    // The manifest as it is published: indented UTF-8 JSON, fields in a fixed order, SHA-256 in lower case.
    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("version", Version.ToString());
            if (PublishedAt is { } published)
            {
                writer.WriteString("publishedAt", published.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            }

            if (ReleaseNotesUrl is { } notes)
            {
                writer.WriteString("releaseNotesUrl", notes.AbsoluteUri);
            }

            writer.WriteStartArray("packages");
            foreach (var package in Packages)
            {
                writer.WriteStartObject();
                writer.WriteString("rid", package.Rid);
                writer.WriteString("url", package.Url.AbsoluteUri);
                writer.WriteString("sha256", package.Sha256);
                writer.WriteNumber("size", package.Size);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (DeltaPackages.Count > 0)
            {
                writer.WriteStartArray("deltas");
                foreach (var delta in DeltaPackages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("rid", delta.Rid);
                    writer.WriteString("baseVersion", delta.BaseVersion.ToString());
                    writer.WriteString("url", delta.Url.AbsoluteUri);
                    writer.WriteString("sha256", delta.Sha256);
                    writer.WriteNumber("size", delta.Size);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}

// What reading a manifest gave.
public enum ReleaseManifestStatus
{
    Valid,

    // A manifest of a later schema than this YAT understands: not an error in it, but nothing this YAT may act on.
    NewerSchema,

    // Not a manifest YAT can use: Problems say why.
    Invalid
}

public sealed record ReleaseManifestReadResult(
    ReleaseManifestStatus Status,
    ReleaseManifest? Manifest,
    IReadOnlyList<string> Problems,
    int? SchemaVersion = null,
    IReadOnlyList<string>? DeltaProblems = null)
{
    public bool IsValid => Status == ReleaseManifestStatus.Valid;
}

// Reads and checks a release manifest. Never throws for what is in the text: anything wrong with it is a result.
//
// Rules: schemaVersion 1 (a later one is NewerSchema, told apart from a broken manifest); a stable Major.Minor.Patch
// version; at least one package, each with a runtime id, unique among the packages ignoring case, an https URL, a SHA-256
// of exactly 64 hex digits (kept in lower case) and a size above 0; publishedAt, when given, an ISO 8601 date and time;
// releaseNotesUrl, when given, https. Optional deltas are validated independently: when strictDeltas is false (default
// client behavior), invalid deltas are ignored while preserving the valid Full ZIP path. Fields it does not know are ignored.
public static class ReleaseManifestReader
{
    public static ReleaseManifestReadResult Read(string json, bool strictDeltas = false)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return Invalid($"The manifest is not JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("The manifest is not a JSON object.");
            }

            if (!root.TryGetProperty("schemaVersion", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schemaVersion)
                || schemaVersion < 1)
            {
                return Invalid("The manifest has no schemaVersion of 1 or more.");
            }

            if (schemaVersion > ReleaseManifest.CurrentSchemaVersion)
            {
                return new ReleaseManifestReadResult(ReleaseManifestStatus.NewerSchema, null, [], schemaVersion);
            }

            var problems = new List<string>();

            ReleaseVersion version = default;
            if (!TryString(root, "version", out var versionText) || !ReleaseVersion.TryParse(versionText, out version))
            {
                problems.Add("The version is not a stable Major.Minor.Patch version.");
            }

            DateTimeOffset? publishedAt = null;
            if (root.TryGetProperty("publishedAt", out var publishedElement) && publishedElement.ValueKind != JsonValueKind.Null)
            {
                if (publishedElement.ValueKind == JsonValueKind.String && publishedElement.TryGetDateTimeOffset(out var published))
                {
                    publishedAt = published;
                }
                else
                {
                    problems.Add("publishedAt is not an ISO 8601 date and time.");
                }
            }

            Uri? releaseNotes = null;
            if (root.TryGetProperty("releaseNotesUrl", out var notesElement) && notesElement.ValueKind != JsonValueKind.Null)
            {
                if (notesElement.ValueKind != JsonValueKind.String || !TryHttps(notesElement.GetString(), out releaseNotes))
                {
                    problems.Add("releaseNotesUrl is not an https URL.");
                }
            }

            var packages = new List<ReleasePackage>();
            if (!root.TryGetProperty("packages", out var packagesElement) || packagesElement.ValueKind != JsonValueKind.Array)
            {
                problems.Add("The manifest has no packages.");
            }
            else
            {
                var position = 0;
                foreach (var element in packagesElement.EnumerateArray())
                {
                    position++;
                    if (ReadPackage(element, position, problems) is not { } package)
                    {
                        continue;
                    }

                    if (packages.Any(other => string.Equals(other.Rid, package.Rid, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add($"Package {position}: runtime '{package.Rid}' is listed twice.");
                        continue;
                    }

                    packages.Add(package);
                }

                if (position == 0)
                {
                    problems.Add("The manifest has no packages.");
                }
            }

            var deltas = new List<ReleaseDeltaPackage>();
            var deltaProblems = new List<string>();
            if (root.TryGetProperty("deltas", out var deltasElement) && deltasElement.ValueKind != JsonValueKind.Null)
            {
                if (deltasElement.ValueKind != JsonValueKind.Array)
                {
                    deltaProblems.Add("The manifest deltas field is not an array.");
                }
                else
                {
                    var deltaPos = 0;
                    foreach (var element in deltasElement.EnumerateArray())
                    {
                        deltaPos++;
                        if (ReadDeltaPackage(element, deltaPos, version, problems.Count == 0, deltaProblems) is not { } delta)
                        {
                            continue;
                        }

                        if (deltas.Any(other =>
                                string.Equals(other.Rid, delta.Rid, StringComparison.OrdinalIgnoreCase)
                                && other.BaseVersion == delta.BaseVersion))
                        {
                            deltaProblems.Add($"Delta {deltaPos}: runtime '{delta.Rid}' and baseVersion '{delta.BaseVersion}' is listed twice.");
                            continue;
                        }

                        deltas.Add(delta);
                    }
                }
            }

            if (strictDeltas && deltaProblems.Count > 0)
            {
                problems.AddRange(deltaProblems);
            }

            return problems.Count > 0
                ? new ReleaseManifestReadResult(ReleaseManifestStatus.Invalid, null, problems, schemaVersion, deltaProblems)
                : new ReleaseManifestReadResult(
                    ReleaseManifestStatus.Valid,
                    new ReleaseManifest(version, packages, publishedAt, releaseNotes, deltas),
                    [],
                    schemaVersion,
                    deltaProblems);
        }
    }

    // A SHA-256 as the manifest writes it: 64 hex digits, in lower case. Null when the text is not one.
    public static string? NormalizeSha256(string? text) =>
        text is { Length: 64 } && text.All(char.IsAsciiHexDigit) ? text.ToLowerInvariant() : null;

    // An absolute https URL; nothing else.
    public static bool TryHttps(string? text, out Uri? url)
    {
        url = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    private static ReleasePackage? ReadPackage(JsonElement element, int position, List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"Package {position} is not an object.");
            return null;
        }

        var count = problems.Count;
        if (!TryString(element, "rid", out var rid) || string.IsNullOrWhiteSpace(rid) || rid != rid.Trim())
        {
            problems.Add($"Package {position} has no runtime id.");
        }

        Uri? url = null;
        if (!TryString(element, "url", out var urlText) || !TryHttps(urlText, out url))
        {
            problems.Add($"Package {position}: the url is not an https URL.");
        }

        var sha256 = TryString(element, "sha256", out var shaText) ? NormalizeSha256(shaText) : null;
        if (sha256 is null)
        {
            problems.Add($"Package {position}: the sha256 is not 64 hex digits.");
        }

        long size = 0;
        if (!element.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out size) || size <= 0)
        {
            problems.Add($"Package {position}: the size is not a whole number above 0.");
        }

        return problems.Count == count ? new ReleasePackage(rid!, url!, sha256!, size) : null;
    }

    private static ReleaseDeltaPackage? ReadDeltaPackage(
        JsonElement element,
        int position,
        ReleaseVersion targetVersion,
        bool hasValidTargetVersion,
        List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"Delta {position} is not an object.");
            return null;
        }

        var count = problems.Count;
        if (!TryString(element, "rid", out var rid) || string.IsNullOrWhiteSpace(rid) || rid != rid.Trim())
        {
            problems.Add($"Delta {position} has no runtime id.");
        }

        ReleaseVersion baseVersion = default;
        if (!TryString(element, "baseVersion", out var baseText) || !ReleaseVersion.TryParse(baseText, out baseVersion))
        {
            problems.Add($"Delta {position}: baseVersion is not a stable Major.Minor.Patch version.");
        }
        else if (hasValidTargetVersion && baseVersion >= targetVersion)
        {
            problems.Add($"Delta {position}: baseVersion '{baseVersion}' must be older than '{targetVersion}'.");
        }

        Uri? url = null;
        if (!TryString(element, "url", out var urlText) || !TryHttps(urlText, out url))
        {
            problems.Add($"Delta {position}: the url is not an https URL.");
        }

        var sha256 = TryString(element, "sha256", out var shaText) ? NormalizeSha256(shaText) : null;
        if (sha256 is null)
        {
            problems.Add($"Delta {position}: the sha256 is not 64 hex digits.");
        }

        long size = 0;
        if (!element.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out size) || size <= 0)
        {
            problems.Add($"Delta {position}: the size is not a whole number above 0.");
        }

        return problems.Count == count ? new ReleaseDeltaPackage(rid!, baseVersion, url!, sha256!, size) : null;
    }

    private static bool TryString(JsonElement element, string name, out string? value)
    {
        value = element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return value is not null;
    }

    private static ReleaseManifestReadResult Invalid(string problem) => new(ReleaseManifestStatus.Invalid, null, [problem]);
}
