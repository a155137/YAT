using System.Text.RegularExpressions;
using YAT.Application.Exceptions;

namespace YAT.Infrastructure.Persistence.DuckDb;

// Physical names are derived only from Guids, never from user text such as headers.
internal static partial class DuckDbIdentifiers
{
    public const string RowIndexColumn = "row_index";

    public static string BlockTable(Guid blockId) => $"blk_{blockId:N}";

    public static string ValueColumn(Guid columnId) => $"c_{columnId:N}";

    // Every physical name placed in SQL, including names read back from the catalog, must pass this check.
    public static string Quote(string physicalName)
    {
        if (!PhysicalName().IsMatch(physicalName))
        {
            throw new RawDataStorageException($"The raw data catalog contains an unexpected physical name '{physicalName}'.");
        }

        return $"\"{physicalName}\"";
    }

    [GeneratedRegex("^(blk|c)_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalName();
}
