namespace YAT.Application.Exceptions;

public sealed class EntityNotFoundException : Exception
{
    public EntityNotFoundException(string entityName, Guid id)
        : base($"{entityName} '{id}' was not found.")
    {
        EntityName = entityName;
        Id = id;
    }

    public string EntityName { get; }

    public Guid Id { get; }
}
