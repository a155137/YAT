using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Features.Worksheets.RenameWorksheet;

public sealed class RenameWorksheetHandler
{
    private readonly IWorksheetRepository _worksheets;
    private readonly TimeProvider _timeProvider;

    public RenameWorksheetHandler(IWorksheetRepository worksheets, TimeProvider timeProvider)
    {
        _worksheets = worksheets;
        _timeProvider = timeProvider;
    }

    // Stores and returns a renamed copy of the worksheet; the stored instance is replaced through the repository,
    // never modified in place. The name must stay unique within the worksheet's project.
    public async Task<Worksheet> HandleAsync(RenameWorksheetCommand command, CancellationToken cancellationToken = default)
    {
        var name = WorksheetNames.Normalize(command.Name);

        var worksheet = await _worksheets.GetByIdAsync(command.WorksheetId, cancellationToken)
            ?? throw new EntityNotFoundException(nameof(Worksheet), command.WorksheetId);

        await WorksheetNames.EnsureUniqueAsync(_worksheets, worksheet.ProjectId, name, worksheet.Id, cancellationToken);

        var renamed = new Worksheet
        {
            Id = worksheet.Id,
            ProjectId = worksheet.ProjectId,
            Name = name,
            RowCount = worksheet.RowCount,
            ColumnCount = worksheet.ColumnCount,
            CreatedAt = worksheet.CreatedAt,
            UpdatedAt = _timeProvider.GetUtcNow()
        };

        await _worksheets.UpdateAsync(renamed, cancellationToken);

        return renamed;
    }
}
