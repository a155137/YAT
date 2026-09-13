using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Features.Worksheets.AddWorksheetColumn;

public sealed class AddWorksheetColumnHandler
{
    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _columns;

    public AddWorksheetColumnHandler(IWorksheetRepository worksheets, IWorksheetColumnRepository columns)
    {
        _worksheets = worksheets;
        _columns = columns;
    }

    public async Task<WorksheetColumn> HandleAsync(AddWorksheetColumnCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Index < 0)
        {
            throw new ValidationException("Column index must not be negative.");
        }

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new ValidationException("Column name must not be empty.");
        }

        var worksheet = await _worksheets.GetByIdAsync(command.WorksheetId, cancellationToken);
        if (worksheet is null)
        {
            throw new EntityNotFoundException(nameof(Worksheet), command.WorksheetId);
        }

        var column = new WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = command.WorksheetId,
            Index = command.Index,
            Name = command.Name.Trim(),
            DataType = command.DataType,
            SemanticType = command.SemanticType,
            Unit = command.Unit
        };

        await _columns.AddAsync(column, cancellationToken);

        return column;
    }
}
