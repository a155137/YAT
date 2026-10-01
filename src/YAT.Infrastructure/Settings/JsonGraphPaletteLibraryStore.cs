using System.Globalization;
using System.Security;
using System.Text.Json;
using YAT.Application.Abstractions.Settings;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;

namespace YAT.Infrastructure.Settings;

// The user's graph palettes in one JSON file (Task #050) - by default graph-palettes.json in YAT's folder of the user's
// roaming application data, never beside the program, so a new release unpacked over the old one, or another version
// run beside it, finds them where they were.
//
//     { "schemaVersion": 1, "defaultPaletteId": null | "<guid>",
//       "palettes": [ { "id": "<guid>", "name": "My Palette", "colors": ["#RRGGBB", ...] } ] }
//
// Reading is forgiving, palette by palette: a palette that breaks a rule (no id, an id or name already read, the
// reserved name, a colour that is not "#RRGGBB", not 1 to 16 colours) is left out and the rest are used; a default that
// is not among them is YAT Default. A file that is no palette library at all is set aside as
// graph-palettes.corrupt-<time>.json, so nothing is lost, and the library starts empty. Loading never writes the file and
// never throws. A file from a newer YAT is read as far as this one understands it and is not written this session.
//
// Writing replaces the file whole: the JSON goes to a temporary file in the same folder, is flushed to disk, and then
// takes the file's place in one rename. Until that rename the old file is untouched, and the rename either happens or
// does not, so a failure anywhere leaves the last good file as it was - never half a file.
public sealed class JsonGraphPaletteLibraryStore : IGraphPaletteLibraryStore
{
    public const string FileName = "graph-palettes.json";

    // The schema this YAT writes and understands in full.
    public const int CurrentSchemaVersion = 1;

    private readonly TimeProvider _timeProvider;

    public JsonGraphPaletteLibraryStore(string filePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // %APPDATA%\YAT\graph-palettes.json, wherever the user's roaming application data is.
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YAT", FileName);

    public string FilePath { get; }

    // Test-only fault injection between the steps of a save. Always null in production.
    internal Action<PaletteWritePhase>? WritePhaseHook { get; set; }

    public GraphPaletteLibraryLoadResult Load()
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(FilePath))
            {
                return GraphPaletteLibraryLoadResult.Empty;
            }

            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return new GraphPaletteLibraryLoadResult(
                GraphPaletteLibrary.Empty,
                true,
                [new GraphPaletteLoadWarning(GraphPaletteLoadWarningKind.FileUnreadable, exception.Message)]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return Corrupt("The file is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Corrupt("The file is not a palette library.");
            }

            var version = CurrentSchemaVersion;
            if (root.TryGetProperty("schemaVersion", out var versionElement))
            {
                if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out version) || version < 1)
                {
                    return Corrupt("The file's schema version is not one YAT writes.");
                }
            }

            var warnings = new List<GraphPaletteLoadWarning>();
            var readOnly = false;
            if (version > CurrentSchemaVersion)
            {
                warnings.Add(new GraphPaletteLoadWarning(
                    GraphPaletteLoadWarningKind.NewerSchema, version.ToString(CultureInfo.InvariantCulture)));
                readOnly = true;
            }

            // The one place an older schema would be brought up to date before it is read. Version 1 is the first, so
            // every version this YAT can meet is read as version 1 (a newer one as far as it is understood).
            if (!TryReadVersion1(root, warnings, out var library))
            {
                return Corrupt("The file is not a palette library.");
            }

            return new GraphPaletteLibraryLoadResult(library, readOnly, warnings);
        }
    }

    public void Save(GraphPaletteLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        var bytes = Serialize(library);
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);

            // Beside the file, so the rename below stays within one volume.
            temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var half = bytes.Length / 2;
                stream.Write(bytes, 0, half);
                WritePhaseHook?.Invoke(PaletteWritePhase.WritingTemporaryFile);
                stream.Write(bytes, half, bytes.Length - half);
                stream.Flush(flushToDisk: true);
            }

            WritePhaseHook?.Invoke(PaletteWritePhase.BeforeReplace);
            File.Move(temporary, FilePath, overwrite: true);
            temporary = null;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            throw new GraphPaletteStoreException($"The graph palettes could not be saved: {exception.Message}", exception);
        }
        finally
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }
        }
    }

    private static bool TryReadVersion1(JsonElement root, List<GraphPaletteLoadWarning> warnings, out GraphPaletteLibrary library)
    {
        library = GraphPaletteLibrary.Empty;
        var palettes = new List<GraphPaletteDefinition>();

        if (root.TryGetProperty("palettes", out var list) && list.ValueKind != JsonValueKind.Null)
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var position = 0;
            foreach (var element in list.EnumerateArray())
            {
                position++;
                if (ReadPalette(element, out var palette, out var reason)
                    && GraphPaletteLibrary.Check(palettes, palette!) is null)
                {
                    palettes.Add(palette!);
                    continue;
                }

                var why = reason ?? (GraphPaletteLibrary.Check(palettes, palette!) switch
                {
                    GraphPaletteLibraryProblem.IdTaken => "its id is another palette's",
                    GraphPaletteLibraryProblem.NameTaken => "its name is another palette's",
                    GraphPaletteLibraryProblem.NameReserved => $"its name is {GraphPaletteLibrary.YatDefaultName}'s",
                    GraphPaletteLibraryProblem.NameMissing => "it has no name",
                    _ => "it does not have 1 to 16 colours"
                });
                warnings.Add(new GraphPaletteLoadWarning(
                    GraphPaletteLoadWarningKind.PaletteSkipped, $"palette {position} ({palette?.Name ?? "unnamed"}): {why}"));
            }
        }

        Guid? defaultPaletteId = null;
        if (root.TryGetProperty("defaultPaletteId", out var defaultElement) && defaultElement.ValueKind != JsonValueKind.Null)
        {
            if (defaultElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(defaultElement.GetString(), out var id)
                && palettes.Any(palette => palette.Id == id))
            {
                defaultPaletteId = id;
            }
            else
            {
                warnings.Add(new GraphPaletteLoadWarning(
                    GraphPaletteLoadWarningKind.DefaultPaletteMissing, defaultElement.ToString()));
            }
        }

        library = new GraphPaletteLibrary(palettes, defaultPaletteId);
        return true;
    }

    // One palette as written, or why it cannot be one. A palette that is read may still break a rule against the
    // palettes read before it, which the library checks.
    private static bool ReadPalette(JsonElement element, out GraphPaletteDefinition? palette, out string? reason)
    {
        palette = null;
        reason = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            reason = "it is not a palette";
            return false;
        }

        if (!element.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var id) || id == Guid.Empty)
        {
            reason = "it has no id";
            return false;
        }

        var name = element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? GraphPaletteLibrary.Normalize(nameElement.GetString())
            : string.Empty;

        if (!element.TryGetProperty("colors", out var colorsElement) || colorsElement.ValueKind != JsonValueKind.Array)
        {
            reason = "it has no colours";
            return false;
        }

        var colors = new List<GraphColor>();
        foreach (var colorElement in colorsElement.EnumerateArray())
        {
            if (colorElement.ValueKind != JsonValueKind.String || !GraphColor.TryParse(colorElement.GetString(), out var color))
            {
                reason = $"'{colorElement}' is not a colour";
                palette = new GraphPaletteDefinition(id, name, new GraphPalette([]));
                return false;
            }

            colors.Add(color);
        }

        palette = new GraphPaletteDefinition(id, name, new GraphPalette(colors));
        return true;
    }

    private static byte[] Serialize(GraphPaletteLibrary library)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            if (library.DefaultPaletteId is { } id)
            {
                writer.WriteString("defaultPaletteId", id.ToString("D"));
            }
            else
            {
                writer.WriteNull("defaultPaletteId");
            }

            writer.WriteStartArray("palettes");
            foreach (var palette in library.CustomPalettes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", palette.Id.ToString("D"));
                writer.WriteString("name", palette.Name);
                writer.WriteStartArray("colors");
                foreach (var color in palette.Palette.Colors)
                {
                    writer.WriteStringValue(color.ToString());
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // The file is set aside under a name of its own, so nothing is lost, and the library starts empty. When it cannot
    // be set aside it stays where it is, and the session is read-only, so it is not overwritten either.
    private GraphPaletteLibraryLoadResult Corrupt(string why)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        var stamp = _timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            var preserved = Path.Combine(directory, $"graph-palettes.corrupt-{stamp}.json");
            for (var attempt = 2; File.Exists(preserved); attempt++)
            {
                preserved = Path.Combine(directory, $"graph-palettes.corrupt-{stamp}-{attempt}.json");
            }

            File.Move(FilePath, preserved);
            return new GraphPaletteLibraryLoadResult(
                GraphPaletteLibrary.Empty,
                false,
                [new GraphPaletteLoadWarning(GraphPaletteLoadWarningKind.FileCorrupt, $"{why} It was kept as {Path.GetFileName(preserved)}.")]);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return new GraphPaletteLibraryLoadResult(
                GraphPaletteLibrary.Empty,
                true,
                [new GraphPaletteLoadWarning(GraphPaletteLoadWarningKind.FileCorruptNotPreserved, $"{why} {exception.Message}")]);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            // A temporary file left behind is harmless: it is never read.
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;
}

// The steps of a save, for fault injection in tests.
internal enum PaletteWritePhase
{
    // Half of the JSON is in the temporary file.
    WritingTemporaryFile,

    // The temporary file is complete; the file is about to be replaced.
    BeforeReplace
}
