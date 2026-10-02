using System.Text;
using System.Text.Json;
using YAT.Application.Distribution;

namespace YAT.Updater;

internal enum JournalState
{
    // Moving files: an interrupted installation is rolled back.
    Replacing,

    // The new release is in place and was validated: only the cleanup is left.
    Committed,

    // Rolling back failed: everything is kept for another try.
    RollbackFailed
}

// <installation>\.yat-update\journal.json (Task #051.C): every rename an installation makes, written - whole, to disk -
// before the first one, and its state. Which renames were made is read from the files themselves (a backed-up file is in
// the backup folder; a placed file is in the installation with the new release's size and SHA-256), so the journal is
// never rewritten while files move and an interrupted installation can always be undone - more than once, safely.
internal sealed record InstallJournal(
    ReleaseVersion From,
    ReleaseVersion To,
    JournalState State,
    IReadOnlyList<PlannedMove> Moves,
    IReadOnlyList<string> CreatedDirectories)
{
    public const int SchemaVersion = 1;

    public static InstallJournal For(InstallPlan plan) => new(plan.From, plan.To, JournalState.Replacing, plan.Moves, plan.CreatedDirectories);

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("from", From.ToString());
            writer.WriteString("to", To.ToString());
            writer.WriteString("state", State.ToString());
            writer.WriteStartArray("moves");
            foreach (var move in Moves)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", move.Kind.ToString());
                writer.WriteString("path", move.Path);
                writer.WriteNumber("size", move.Size);
                writer.WriteString("sha256", move.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("createdDirectories");
            foreach (var directory in CreatedDirectories)
            {
                writer.WriteStringValue(directory);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // Written to a temporary file, flushed to disk, then renamed over the journal: never half written.
    public void Write(string path)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(ToJson());
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public InstallJournal With(JournalState state) => this with { State = state };

    // The journal at this path, or UpdaterException when it cannot be one (it is then left where it is).
    public static InstallJournal Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                throw new UpdaterException($"The installation journal {path} is not schema version {SchemaVersion}.");
            }

            var moves = new List<PlannedMove>();
            foreach (var element in root.GetProperty("moves").EnumerateArray())
            {
                var move = new PlannedMove(
                    Enum.Parse<MoveKind>(element.GetProperty("kind").GetString()!),
                    element.GetProperty("path").GetString()!,
                    element.GetProperty("size").GetInt64(),
                    element.GetProperty("sha256").GetString()!);
                if (ReleasePath.Normalize(move.Path) != move.Path || ReleaseInstallation.IsReserved(move.Path))
                {
                    throw new UpdaterException($"The installation journal {path} names '{move.Path}', which is not a release path.");
                }

                moves.Add(move);
            }

            var directories = root.GetProperty("createdDirectories").EnumerateArray().Select(element => element.GetString()!).ToList();
            if (directories.FirstOrDefault(directory => ReleasePath.Normalize(directory) != directory || ReleaseInstallation.IsReserved(directory)) is { } bad)
            {
                throw new UpdaterException($"The installation journal {path} names '{bad}', which is not a release path.");
            }

            return new InstallJournal(
                ReleaseVersion.Parse(root.GetProperty("from").GetString()!),
                ReleaseVersion.Parse(root.GetProperty("to").GetString()!),
                Enum.Parse<JournalState>(root.GetProperty("state").GetString()!),
                moves,
                directories);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException
            or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new UpdaterException($"The installation journal {path} cannot be read: {exception.Message}", exception);
        }
    }
}
