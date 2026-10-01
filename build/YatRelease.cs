#:project ../src/YAT.Infrastructure/YAT.Infrastructure.csproj

// YAT's release steps that need the release rules themselves (Task #051), run by build/publish.ps1 with
// "dotnet run build/YatRelease.cs -- <command> ...": a .NET file-based app over YAT.Application's release types and
// YAT.Infrastructure's ReleaseArtifacts, so the rules a release is made and checked by are the very ones YAT's tests
// check - written once, in C#. Nothing here connects to anything.
//
//     version   <version>                                     the version is a stable Major.Minor.Patch
//     inventory <release folder> <version> <rid>              writes the folder's yat-files.json
//     package   <zip> <version> <rid> [--base-url <https>] [--release-notes <https>]
//                                                             writes <zip>.sha256 and, given a base URL, yat-update.json
//     verify    <output folder> <version> <rid> [--require-manifest]
//                                                             the whole set agrees with itself; lists every problem
//
// Exit code 0 when the step succeeded, 1 with the reasons on standard error when it did not, 2 for a misused command.
using YAT.Application.Distribution;
using YAT.Infrastructure.Distribution;

try
{
    return Run(args);
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static int Run(string[] args)
{
    if (args.Length < 2)
    {
        return Usage();
    }

    // Every command names the version: "version <v>", the others "<command> <path> <v> ...".
    var versionText = args[0] == "version" ? args[1] : args.Length > 2 ? args[2] : null;
    if (versionText is null)
    {
        return Usage();
    }

    if (!ReleaseVersion.TryParse(versionText, out var version))
    {
        Console.Error.WriteLine($"'{versionText}' is not a stable release version (Major.Minor.Patch).");
        return 1;
    }

    switch (args[0])
    {
        case "version":
            Console.WriteLine(version);
            return 0;

        case "inventory" when args.Length == 4:
        {
            var inventory = ReleaseArtifacts.Scan(args[1], version, args[3]);
            ReleaseArtifacts.WriteInventory(args[1], inventory);
            Console.WriteLine($"{ReleaseInventory.FileName}: {inventory.Files.Count} files");
            return 0;
        }

        case "package" when args.Length >= 4:
        {
            var options = Options(args[4..]);
            var hash = ReleaseArtifacts.WriteChecksum(args[1]);
            Console.WriteLine($"{Path.GetFileName(args[1])}: SHA-256 {hash}, {new FileInfo(args[1]).Length} bytes");
            if (options.TryGetValue("--base-url", out var baseUrl))
            {
                options.TryGetValue("--release-notes", out var notes);
                var manifest = ReleaseArtifacts.CreateManifest(version, args[3], args[1], baseUrl, notes, DateTimeOffset.UtcNow);
                var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, ReleaseManifest.FileName);
                ReleaseArtifacts.WriteManifest(path, manifest);
                Console.WriteLine($"{ReleaseManifest.FileName}: {manifest.Packages[0].Url}");
            }
            else
            {
                Console.WriteLine($"{ReleaseManifest.FileName}: not written - no --base-url, so the package's URL is not known.");
            }

            return 0;
        }

        case "verify" when args.Length is 4 or 5:
        {
            var problems = ReleaseArtifacts.Verify(args[1], version, args[3], requireManifest: args.Length == 5 && args[4] == "--require-manifest");
            foreach (var problem in problems)
            {
                Console.Error.WriteLine(problem);
            }

            Console.WriteLine(problems.Count == 0 ? "The release set is consistent." : $"{problems.Count} problem(s).");
            return problems.Count == 0 ? 0 : 1;
        }

        default:
            return Usage();
    }
}

static Dictionary<string, string> Options(string[] rest)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < rest.Length; index += 2)
    {
        if (index + 1 >= rest.Length || rest[index] is not ("--base-url" or "--release-notes"))
        {
            throw new ArgumentException($"Unexpected option '{rest[index]}'.");
        }

        options[rest[index]] = rest[index + 1];
    }

    return options;
}

static int Usage()
{
    Console.Error.WriteLine("usage: YatRelease version <v> | inventory <dir> <v> <rid> | package <zip> <v> <rid> [--base-url <https>] [--release-notes <https>] | verify <dir> <v> <rid> [--require-manifest]");
    return 2;
}
