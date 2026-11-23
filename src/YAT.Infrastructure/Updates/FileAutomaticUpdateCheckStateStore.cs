using System.Globalization;
using System.Text.Json;
using YAT.Application.Updates;

namespace YAT.Infrastructure.Updates;

// Lightweight JSON persistence for the last automatic startup update check date under %LOCALAPPDATA%\YAT\updates.
// Corrupt or unreadable state files are treated as "not checked yet" and never block startup.
public sealed class FileAutomaticUpdateCheckStateStore : IAutomaticUpdateCheckStateStore
{
    private const string FileName = "auto-check-state.json";
    private readonly string _filePath;

    public FileAutomaticUpdateCheckStateStore(string? updatesRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(updatesRoot)
            ? UpdatePackageStore.DefaultRoot
            : updatesRoot;
        _filePath = Path.Combine(root, FileName);
    }

    public DateOnly? ReadLastCheckedDate()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json = File.ReadAllText(_filePath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("lastCheckedDate", out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return DateOnly.TryParseExact(property.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void WriteLastCheckedDate(DateOnly date)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = JsonSerializer.Serialize(new { lastCheckedDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
            var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, payload);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            // Startup check state is best-effort; storage errors must never crash YAT.
        }
    }
}
