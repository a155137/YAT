using System.Text;
using YAT.Application.Distribution;

namespace YAT.Updater;

// The release installed in a folder (Task #051.C): its yat-files.json - which says which of the folder's files are the
// release's own - for the version YAT said it was, for win-x64. An installation that is a link, or has no such
// inventory, is not updated.
internal sealed record InstalledRelease(string Folder, ReleaseInventory Inventory)
{
    public static InstalledRelease Read(string installation, ReleaseVersion expected)
    {
        var folder = new DirectoryInfo(installation);
        if (!folder.Exists)
        {
            throw new UpdaterException($"The installation {installation} does not exist.");
        }

        if (folder.LinkTarget is not null)
        {
            throw new UpdaterException($"The installation {installation} is a link; YAT updates only a real folder.");
        }

        var path = ReleaseInstallation.InventoryPath(installation);
        string json;
        try
        {
            if (new FileInfo(path).Length > ReleasePackageVerifier.MaximumInventoryBytes)
            {
                throw new UpdaterException($"{path} is too large to be an inventory.");
            }

            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdaterException($"The installed release's {ReleaseInventory.FileName} cannot be read: {exception.Message}", exception);
        }

        if (!ReleaseInventory.TryRead(json, out var inventory, out var problem))
        {
            throw new UpdaterException($"The installed release's {ReleaseInventory.FileName} is not valid: {problem}");
        }

        if (inventory!.Version != expected || inventory.Rid != ReleaseInstallation.Rid)
        {
            throw new UpdaterException($"The installed release is {inventory.Version} {inventory.Rid}, not {expected} {ReleaseInstallation.Rid}.");
        }

        if (inventory.Files.FirstOrDefault(file => ReleaseInstallation.IsReserved(file.Path)) is { } reserved)
        {
            throw new UpdaterException($"The installed release claims {reserved.Path}, which belongs to the updater.");
        }

        return new InstalledRelease(Path.GetFullPath(installation), inventory);
    }
}
