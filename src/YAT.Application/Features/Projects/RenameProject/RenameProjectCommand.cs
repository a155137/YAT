namespace YAT.Application.Features.Projects.RenameProject;

public sealed record RenameProjectCommand(Guid ProjectId, string Name);
