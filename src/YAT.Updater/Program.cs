using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Updater;

internal static class ExitCodes
{
    public const int Done = 0;
    public const int Failed = 1;
    public const int RollbackFailed = 2;
    public const int Usage = 3;
    public const int Refused = 4;
}

// YAT.Updater.exe (Task #051.C): `install` - the handshake with YAT, then the installation - or `recover`.
internal static class Program
{
    public static int Main(string[] args)
    {
        if (!UpdaterArguments.TryParse(args, out var arguments, out var problem))
        {
            TryRefuse(args, problem!);
            return ExitCodes.Usage;
        }

        var log = new UpdaterLog(ReleaseInstallation.LogPath(arguments!.InstallDirectory));
        var host = new WindowsHost();
        try
        {
            if (!arguments.IsInstall)
            {
                using var held = InstallLock.TryAcquire(arguments.InstallDirectory);
                if (held is null)
                {
                    host.Notify("A YAT update is running for this folder. Wait for it to finish.", error: true);
                    return ExitCodes.Refused;
                }

                return Code(new Installer(arguments.InstallDirectory, updatesRoot: null, host, log).Recover());
            }

            using var handshake = Handshake.Open(arguments.PipeIn!, arguments.PipeOut!);
            return new InstallSession(log, host).Run(
                arguments,
                handshake,
                ReleaseInstallation.Executable(arguments.InstallDirectory),
                () => new Installer(arguments.InstallDirectory, arguments.UpdatesRoot!, host, log).Install(arguments.FromVersion!.Value, arguments.Version!.Value));
        }
        catch (Exception exception)
        {
            log.Write($"Unexpected failure: {exception}");
            host.Notify(
                $"The YAT update stopped unexpectedly.\n\n{exception.Message}\n\nIf YAT does not start, run:\n\"{ReleaseInstallation.UpdaterPath(arguments.InstallDirectory)}\" recover --protocol 1 --install-dir \"{arguments.InstallDirectory}\"\n\nDetails: {ReleaseInstallation.LogPath(arguments.InstallDirectory)}",
                error: true);
            return ExitCodes.Failed;
        }
    }

    public static int Code(InstallOutcome outcome) => outcome switch
    {
        InstallOutcome.Installed => ExitCodes.Done,
        InstallOutcome.RollbackFailed => ExitCodes.RollbackFailed,
        _ => ExitCodes.Failed
    };

    // Tells YAT why, when the arguments name a pipe to tell it on.
    private static void TryRefuse(string[] args, string problem)
    {
        var index = Array.IndexOf(args, UpdateInstallProtocol.PipeOutOption);
        if (index < 0 || index + 1 >= args.Length || !args[index + 1].All(char.IsAsciiDigit))
        {
            return;
        }

        try
        {
            using var output = new AnonymousPipeClientStream(PipeDirection.Out, args[index + 1]);
            using var writer = new StreamWriter(output);
            writer.WriteLine(UpdateInstallProtocol.ErrorPrefix + problem);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
    }
}

// From "ready" to the end of the installation (Task #051.C): the YAT that asked is identified, the install lock taken,
// "ready" said; on "go", once that YAT has exited, the installation runs. Before that, nothing in the installation is
// changed - a refusal, "cancel" or a pipe that closes ends the updater.
internal sealed class InstallSession(UpdaterLog log, IUpdaterHost host)
{
    public TimeSpan ExitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int Run(UpdaterArguments arguments, Handshake handshake, string expectedExecutable, Func<InstallOutcome> install)
    {
        TargetProcess target;
        try
        {
            target = TargetProcess.Open(arguments.ProcessId, arguments.ProcessStart, expectedExecutable);
        }
        catch (UpdaterException exception)
        {
            log.Write($"Refused: {exception.Message}");
            handshake.Refuse(exception.Message);
            return ExitCodes.Refused;
        }

        using (target)
        {
            InstallLock? held;
            try
            {
                held = InstallLock.TryAcquire(arguments.InstallDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                handshake.Refuse($"YAT's folder cannot be written: {exception.Message}");
                return ExitCodes.Refused;
            }

            if (held is null)
            {
                log.Write("Refused: another installation holds the lock.");
                handshake.Refuse("Another update of this YAT is in progress.");
                return ExitCodes.Refused;
            }

            using (held)
            {
                log.Write($"Ready to install {arguments.Version} over {arguments.FromVersion} for process {target.Id}.");
                handshake.Ready();
                var answer = handshake.WaitForAnswer();
                log.Write($"YAT answered: {answer}.");
                if (answer != HandshakeAnswer.Go)
                {
                    return ExitCodes.Done;
                }

                var waiting = Stopwatch.StartNew();
                if (!target.WaitForExit(ExitTimeout))
                {
                    log.Write("YAT did not exit in time; nothing was changed.");
                    host.Notify($"YAT did not close within {ExitTimeout.TotalSeconds:F0} seconds, so the update was not installed. Nothing has been changed.", error: true);
                    return ExitCodes.Failed;
                }

                log.Write($"Timing: YAT exited {waiting.ElapsedMilliseconds} ms after go.");
                return Program.Code(install());
            }
        }
    }
}

// The updater's Windows: message boxes, starting YAT, file versions, processes.
internal sealed class WindowsHost : IUpdaterHost
{
    private const uint Ok = 0x0;
    private const uint IconError = 0x10;
    private const uint IconInformation = 0x40;
    private const uint SetForeground = 0x10000;
    private const uint TopMost = 0x40000;

    public void Notify(string message, bool error) =>
        MessageBoxW(IntPtr.Zero, message, "YAT Update", Ok | (error ? IconError : IconInformation) | SetForeground | TopMost);

    public bool Start(string executable, string workingDirectory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory
            });
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    public string? FileVersionOf(string executable)
    {
        try
        {
            return File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).FileVersion : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public IReadOnlyList<int> OthersRunning(string executable) => TargetProcess.OthersRunning(executable);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
