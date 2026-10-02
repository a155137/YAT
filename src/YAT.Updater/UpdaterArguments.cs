using System.Globalization;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Updater;

// The updater's command line (UpdateInstallProtocol), checked before anything else is done: a known command, protocol 1,
// every option once and nothing unknown, full local paths, stable versions, a process id and start time, pipe handles.
internal sealed record UpdaterArguments(
    string Command,
    string InstallDirectory,
    string? UpdatesRoot,
    ReleaseVersion? Version,
    ReleaseVersion? FromVersion,
    int ProcessId,
    long ProcessStart,
    string? PipeIn,
    string? PipeOut)
{
    public bool IsInstall => Command == UpdateInstallProtocol.InstallCommand;

    public static bool TryParse(IReadOnlyList<string> args, out UpdaterArguments? parsed, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        parsed = null;
        problem = null;

        if (args.Count == 0 || args[0] is not (UpdateInstallProtocol.InstallCommand or UpdateInstallProtocol.RecoverCommand))
        {
            problem = "The command must be install or recover.";
            return false;
        }

        var command = args[0];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] known = command == UpdateInstallProtocol.InstallCommand
            ?
            [
                UpdateInstallProtocol.ProtocolOption, UpdateInstallProtocol.InstallDirectoryOption, UpdateInstallProtocol.UpdatesRootOption,
                UpdateInstallProtocol.VersionOption, UpdateInstallProtocol.FromVersionOption, UpdateInstallProtocol.ProcessIdOption,
                UpdateInstallProtocol.ProcessStartOption, UpdateInstallProtocol.PipeInOption, UpdateInstallProtocol.PipeOutOption
            ]
            : [UpdateInstallProtocol.ProtocolOption, UpdateInstallProtocol.InstallDirectoryOption];

        for (var index = 1; index < args.Count; index += 2)
        {
            var name = args[index];
            if (!known.Contains(name))
            {
                problem = $"Unknown option '{name}'.";
                return false;
            }

            if (index + 1 >= args.Count)
            {
                problem = $"The option {name} has no value.";
                return false;
            }

            if (!options.TryAdd(name, args[index + 1]))
            {
                problem = $"The option {name} is given twice.";
                return false;
            }
        }

        if (options.Count != known.Length)
        {
            problem = $"Missing option(s): {string.Join(", ", known.Where(name => !options.ContainsKey(name)))}.";
            return false;
        }

        if (options[UpdateInstallProtocol.ProtocolOption] != UpdateInstallProtocol.Version.ToString(CultureInfo.InvariantCulture))
        {
            problem = $"Protocol {options[UpdateInstallProtocol.ProtocolOption]} is not supported; this updater speaks protocol {UpdateInstallProtocol.Version}.";
            return false;
        }

        if (!TryFolder(options[UpdateInstallProtocol.InstallDirectoryOption], out var installation))
        {
            problem = "The installation folder must be a full local path.";
            return false;
        }

        if (command == UpdateInstallProtocol.RecoverCommand)
        {
            parsed = new UpdaterArguments(command, installation!, null, null, null, 0, 0, null, null);
            return true;
        }

        if (!TryFolder(options[UpdateInstallProtocol.UpdatesRootOption], out var updates))
        {
            problem = "The updates folder must be a full local path.";
            return false;
        }

        if (!ReleaseVersion.TryParse(options[UpdateInstallProtocol.VersionOption], out var version)
            || !ReleaseVersion.TryParse(options[UpdateInstallProtocol.FromVersionOption], out var from))
        {
            problem = "The versions must be stable release versions.";
            return false;
        }

        if (!int.TryParse(options[UpdateInstallProtocol.ProcessIdOption], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0
            || !long.TryParse(options[UpdateInstallProtocol.ProcessStartOption], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start <= 0)
        {
            problem = "The process id and start time must be positive numbers.";
            return false;
        }

        var pipeIn = options[UpdateInstallProtocol.PipeInOption];
        var pipeOut = options[UpdateInstallProtocol.PipeOutOption];
        if (!IsHandle(pipeIn) || !IsHandle(pipeOut))
        {
            problem = "The pipe handles must be numbers.";
            return false;
        }

        parsed = new UpdaterArguments(command, installation!, updates, version, from, processId, start, pipeIn, pipeOut);
        return true;
    }

    // A fully qualified local folder path (a drive, or a UNC share), normalized, without a trailing separator.
    private static bool TryFolder(string text, out string? folder)
    {
        folder = null;
        if (string.IsNullOrWhiteSpace(text) || !Path.IsPathFullyQualified(text) || text.StartsWith(@"\\?\", StringComparison.Ordinal)
            || text.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsHandle(string text) =>
        text.Length is > 0 and <= 20 && text.All(char.IsAsciiDigit);
}
