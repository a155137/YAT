using YAT.Domain.Entities;

namespace YAT.Application.Abstractions.Persistence;

public interface IWorksheetColumnRepository
{
    Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken);
}
