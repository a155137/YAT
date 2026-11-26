using System.Text.Json;
using System.Text.Json.Serialization;
using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.ImportProfiles;

namespace YAT.Infrastructure.ImportProfiles;

// Explicit System.Text.Json serialization for ImportProfile (FT-01A).
// Preserves exact string values (including leading-zero Bins and custom missing markers) and rejects unsupported schema versions.
public static class ImportProfileJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static byte[] Serialize(ImportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.SerializeToUtf8Bytes(profile, SerializerOptions);
    }

    public static ImportProfile Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.MalformedJson, "The profile file is not valid JSON.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ImportProfileStoreException(ImportProfileStoreError.MalformedJson, "The profile JSON root must be an object.");
            }

            if (!doc.RootElement.TryGetProperty("schemaVersion", out var versionProp)
                || versionProp.ValueKind != JsonValueKind.Number
                || !versionProp.TryGetInt32(out var schemaVersion))
            {
                throw new ImportProfileStoreException(ImportProfileStoreError.MalformedJson, "The profile JSON is missing a valid integer schemaVersion.");
            }

            if (schemaVersion != ImportProfile.CurrentSchemaVersion)
            {
                throw new ImportProfileStoreException(
                    ImportProfileStoreError.UnsupportedSchemaVersion,
                    $"Profile schema version {schemaVersion} is not supported; supported version is {ImportProfile.CurrentSchemaVersion}.");
            }
        }

        try
        {
            var profile = JsonSerializer.Deserialize<ImportProfile>(utf8Json, SerializerOptions);
            if (profile is null)
            {
                throw new ImportProfileStoreException(ImportProfileStoreError.MalformedJson, "The profile JSON deserialized to null.");
            }

            return profile;
        }
        catch (JsonException ex)
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.MalformedJson, "The profile JSON does not match the expected structure.", ex);
        }
    }
}
