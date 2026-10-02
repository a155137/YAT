namespace YAT.Application.Updates;

// How YAT and its updater talk (Task #051.C), versioned so an older YAT and a newer updater agree or refuse:
//
//     YAT.Updater.exe install --protocol 1 --install-dir <installation> --updates-root <updates>
//                             --version <new> --from-version <installed> --pid <YAT's id> --pid-start <its start, UTC ticks>
//                             --pipe-in <handle> --pipe-out <handle>
//     YAT.Updater.exe recover --protocol 1 --install-dir <installation>
//
// Arguments are passed as a list (never through a shell). The pipes are anonymous, inherited by the updater: it writes
// one line - "ready", or "error <reason>" - and reads one: "go" (YAT is closing; install once it has exited) or "cancel"
// (it is not; change nothing). A pipe that closes without "go" means YAT went away without asking: nothing is installed.
public static class UpdateInstallProtocol
{
    public const int Version = 1;

    public const string InstallCommand = "install";

    public const string RecoverCommand = "recover";

    public const string ProtocolOption = "--protocol";

    public const string InstallDirectoryOption = "--install-dir";

    public const string UpdatesRootOption = "--updates-root";

    public const string VersionOption = "--version";

    public const string FromVersionOption = "--from-version";

    public const string ProcessIdOption = "--pid";

    public const string ProcessStartOption = "--pid-start";

    public const string PipeInOption = "--pipe-in";

    public const string PipeOutOption = "--pipe-out";

    public const string Ready = "ready";

    public const string Go = "go";

    public const string Cancel = "cancel";

    public const string ErrorPrefix = "error ";
}
