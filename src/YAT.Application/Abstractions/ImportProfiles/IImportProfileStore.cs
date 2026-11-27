using YAT.Application.ImportProfiles;

namespace YAT.Application.Abstractions.ImportProfiles;

public interface IImportProfileSerializer
{
    byte[] Serialize(ImportProfile profile);

    ImportProfile Deserialize(ReadOnlySpan<byte> utf8Json);
}

public interface IImportProfileStore
{
    string? StorageDirectoryPath => null;

    ImportProfileStoreSnapshot Enumerate();

    ImportProfile? Load(Guid id);

    void Create(ImportProfile profile);

    void Update(ImportProfile profile);

    void Delete(Guid id);
}

public sealed record ImportProfileStoreSnapshot(
    IReadOnlyList<ImportProfile> Profiles,
    IReadOnlyList<ImportProfileLoadIssue> Issues)
{
    public static ImportProfileStoreSnapshot Empty { get; } = new([], []);
}

public enum ImportProfileLoadIssueKind
{
    FileUnreadable,
    MalformedJson,
    UnsupportedSchemaVersion,
    InvalidProfile,
    DuplicateId
}

public sealed record ImportProfileLoadIssue(
    ImportProfileLoadIssueKind Kind,
    string FileName,
    string Detail);

public enum ImportProfileStoreError
{
    InvalidProfile,
    AlreadyExists,
    DuplicateIdentity,
    NotFound,
    UnsupportedSchemaVersion,
    MalformedJson,
    StorageFailure
}

public sealed class ImportProfileStoreException : Exception
{
    public ImportProfileStoreException(ImportProfileStoreError error, string message)
        : base(message)
    {
        Error = error;
    }

    public ImportProfileStoreException(ImportProfileStoreError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public ImportProfileStoreError Error { get; }
}
