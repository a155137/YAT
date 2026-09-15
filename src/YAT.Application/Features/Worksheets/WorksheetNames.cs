using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;

namespace YAT.Application.Features.Worksheets;

// Worksheet name invariant, shared by create and rename: a trimmed, non-empty name that no other worksheet of the same
// project uses, compared case-insensitively ("Sheet1", "sheet1" and "SHEET1" are the same name).
internal static class WorksheetNames
{
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("Worksheet name must not be empty.");
        }

        return name.Trim();
    }

    // excludedWorksheetId: the worksheet being renamed, which may keep its own name (e.g. a casing-only change).
    public static async Task EnsureUniqueAsync(
        IWorksheetRepository worksheets,
        Guid projectId,
        string name,
        Guid? excludedWorksheetId,
        CancellationToken cancellationToken)
    {
        var projectWorksheets = await worksheets.GetByProjectIdAsync(projectId, cancellationToken);
        if (projectWorksheets.Any(worksheet => worksheet.Id != excludedWorksheetId && string.Equals(worksheet.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException($"A worksheet named '{name}' already exists in this project.");
        }
    }
}
