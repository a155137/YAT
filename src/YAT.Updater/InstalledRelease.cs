using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;

namespace YAT.Updater;

// The release installed in a folder (Task #051.C): its yat-files.json - which says which of the folder's files are the
// release's own - for the version YAT said it was, for win-x64. An installation that is a link, or has no such
// inventory, is not updated.
internal sealed record InstalledRelease(string Folder, ReleaseInventory Inventory, string InventorySha256)
{
    public static InstalledRelease Read(string installation, ReleaseVersion expected)
    {
        var folder = new DirectoryInfo(installation);
        if (!folder.Exists)
        {
            throw new UpdaterException($"The installation {installation} does not exist.");
        }

        if (folder.LinkTarget is not null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UpdaterException($"The installation {installation} is a link; YAT updates only a real folder.");
        }

        var path = ReleaseInstallation.InventoryPath(installation);
        byte[] bytes;
        try
        {
            var inventoryInfo = new FileInfo(path);
            if (inventoryInfo.Exists)
            {
                if (inventoryInfo.LinkTarget is not null || inventoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UpdaterException($"The installed release's {ReleaseInventory.FileName} is a link.");
                }

                if (inventoryInfo.Length > ReleasePackageVerifier.MaximumInventoryBytes)
                {
                    throw new UpdaterException($"{path} is too large to be an inventory.");
                }
            }

            bytes = File.ReadAllBytes(path);
            if (bytes.Length > ReleasePackageVerifier.MaximumInventoryBytes)
            {
                throw new UpdaterException($"{path} is too large to be an inventory.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdaterException($"The installed release's {ReleaseInventory.FileName} cannot be read: {exception.Message}", exception);
        }

        var json = Encoding.UTF8.GetString(bytes);
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

        if (inventory.Files.FirstOrDefault(file => ReleaseInstallation.IsUserData(file.Path)) is { } userData)
        {
            throw new UpdaterException($"The installed release claims {userData.Path}, which belongs to the protected '{ReleaseInstallation.UserDataFolderName}/' directory.");
        }

        var inventorySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new InstalledRelease(Path.GetFullPath(installation), inventory, inventorySha256);
    }
}
