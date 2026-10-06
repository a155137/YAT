using System.Diagnostics;
using System.IO.Pipes;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using static YAT.Updater.Tests.Releases;

namespace YAT.Updater.Tests;

// From YAT starting the updater to the installation (Task #051.C): the YAT that asked is identified by id, start time and
// executable; "ready" only once it is and the install lock is held; nothing is installed without "go" and that YAT's
// exit. The target here is a real process (ping.exe, which waits), so the identity checks are the real ones; the last
// tests run the real YAT.Updater.exe over real anonymous pipes against a fake installation.
public sealed class HandshakeTests : IDisposable
{
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    private readonly Sandbox _sandbox = new();
    private readonly FakeHost _host = new();
    private readonly List<Process> _processes = [];

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        _sandbox.Dispose();
    }

    private Process Waiting(string executable = "")
    {
        var process = Process.Start(new ProcessStartInfo(executable.Length == 0 ? Ping : executable)
        {
            ArgumentList = { "-n", "120", "127.0.0.1" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        _processes.Add(process);

        // A process only just created may not be running its own code yet, and until it is, its main module cannot be
        // read (Process.MainModule is null under load), so the updater would refuse it as running another executable.
        // The YAT the updater checks has long been running; the stand-in is given that too: it is handed over once the
        // module the updater reads can be read. (A copy of ping outside System32 prints nothing, so its output cannot
        // say so.)
        var ready = Stopwatch.StartNew();
        while (!MainModuleReadable(process))
        {
            Assert.True(ready.Elapsed < TimeSpan.FromSeconds(30), "the stand-in for YAT never became identifiable");
            Thread.Sleep(10);
        }

        return process;
    }

    private static bool MainModuleReadable(Process process)
    {
        try
        {
            process.Refresh();
            return process.MainModule?.FileName is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private UpdaterArguments Arguments(Process target, long? start = null) =>
        new("install", _sandbox.Installation, _sandbox.Updates, V3, V2, target.Id, start ?? target.StartTime.ToUniversalTime().Ticks, "1", "2");

    private (int Code, string Said, int Installs) Run(UpdaterArguments arguments, string answer, string expected, TimeSpan? exitTimeout = null, Func<bool>? beforeInstall = null)
    {
        var said = new StringWriter();
        var installs = 0;
        using var handshake = new Handshake(new StringReader(answer), said);
        var code = new InstallSession(_sandbox.Log, _host) { ExitTimeout = exitTimeout ?? TimeSpan.FromSeconds(10) }.Run(arguments, handshake, expected, () =>
        {
            installs++;
            Assert.True(beforeInstall?.Invoke() ?? true);
            return InstallOutcome.Installed;
        });
        return (code, said.ToString(), installs);
    }

    // ---- The session ----

    [Fact]
    public void CancelChangesNothing()
    {
        var target = Waiting();

        var (code, said, installs) = Run(Arguments(target), "cancel\n", Ping);

        Assert.Equal((ExitCodes.Done, "ready" + Environment.NewLine, 0), (code, said, installs));
        Assert.False(File.Exists(ReleaseInstallation.LockPath(_sandbox.Installation)), "the lock is released");
    }

    [Fact]
    public void GoInstallsOnlyOnceYatHasExited()
    {
        var target = Waiting();
        _ = Task.Run(async () =>
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            target.Kill();
        }, TestContext.Current.CancellationToken);

        var (code, said, installs) = Run(Arguments(target), "go\n", Ping, beforeInstall: () => target.HasExited);

        Assert.Equal((ExitCodes.Done, "ready" + Environment.NewLine, 1), (code, said, installs));
    }

    [Fact]
    public void AYatThatDoesNotExitIsNotWaitedForAndNothingIsInstalled()
    {
        var target = Waiting();

        var (code, _, installs) = Run(Arguments(target), "go\n", Ping, exitTimeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal((ExitCodes.Failed, 0), (code, installs));
        Assert.Contains("did not close", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GO\n")]
    [InlineData("yes\n")]
    [InlineData("go now\n")]
    public void APipeClosedOrAnythingButGoInstallsNothing(string answer)
    {
        var target = Waiting();

        var (code, _, installs) = Run(Arguments(target), answer, Ping);

        Assert.Equal((ExitCodes.Done, 0), (code, installs));
    }

    [Fact]
    public void AnotherStartTimeIsAnotherProcess()
    {
        var target = Waiting();

        var (code, said, installs) = Run(Arguments(target, start: target.StartTime.ToUniversalTime().Ticks - 1), "go\n", Ping);

        Assert.Equal((ExitCodes.Refused, 0), (code, installs));
        Assert.StartsWith("error Process", said, StringComparison.Ordinal);
        Assert.Contains("another start time", said, StringComparison.Ordinal);
        Assert.DoesNotContain("ready", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessRunningAnotherExecutableIsRefused()
    {
        var target = Waiting();

        var (code, said, _) = Run(Arguments(target), "go\n", ReleaseInstallation.Executable(_sandbox.Installation));

        Assert.Equal(ExitCodes.Refused, code);
        Assert.Contains("does not run", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessThatIsGoneIsRefused()
    {
        var target = Waiting();
        var arguments = Arguments(target);
        target.Kill();
        target.WaitForExit();

        var (code, said, _) = Run(arguments, "go\n", Ping);

        Assert.Equal(ExitCodes.Refused, code);
        Assert.StartsWith("error ", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondUpdaterForTheSameInstallationIsRefused()
    {
        var target = Waiting();
        using var first = InstallLock.TryAcquire(_sandbox.Installation);

        var (code, said, installs) = Run(Arguments(target), "go\n", Ping);

        Assert.Equal((ExitCodes.Refused, 0), (code, installs));
        Assert.Contains("Another update of this YAT is in progress", said, StringComparison.Ordinal);
    }

    // ---- The pipes ----

    [Fact]
    public void TheAnonymousPipesCarryReadyAndTheAnswer()
    {
        using var toUpdater = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var fromUpdater = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var handshake = Handshake.Open(toUpdater.GetClientHandleAsString(), fromUpdater.GetClientHandleAsString());
        using var reader = new StreamReader(fromUpdater);
        using var writer = new StreamWriter(toUpdater) { AutoFlush = true };

        handshake.Ready();
        Assert.Equal("ready", reader.ReadLine());
        writer.WriteLine("go");

        Assert.Equal(HandshakeAnswer.Go, handshake.WaitForAnswer());
    }

    [Fact]
    public void APipeWhoseOtherEndIsGoneIsGone()
    {
        var toUpdater = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var fromUpdater = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var handshake = Handshake.Open(toUpdater.GetClientHandleAsString(), fromUpdater.GetClientHandleAsString());
        toUpdater.DisposeLocalCopyOfClientHandle();
        toUpdater.Dispose();

        Assert.Equal(HandshakeAnswer.Gone, handshake.WaitForAnswer());
    }

    // ---- The real updater, in its own process ----

    private string FakeYat()
    {
        Install(_sandbox.Installation, V2);
        var executable = ReleaseInstallation.Executable(_sandbox.Installation);
        File.Copy(Ping, executable, overwrite: true);
        return executable;
    }

    private sealed class UpdaterProcess : IDisposable
    {
        private readonly AnonymousPipeServerStream _toUpdater = new(PipeDirection.Out, HandleInheritability.Inheritable);
        private readonly AnonymousPipeServerStream _fromUpdater = new(PipeDirection.In, HandleInheritability.Inheritable);

        public UpdaterProcess(Func<string, string, IEnumerable<string>> arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "YAT.Updater.exe")) { UseShellExecute = false };
            foreach (var argument in arguments(_toUpdater.GetClientHandleAsString(), _fromUpdater.GetClientHandleAsString()))
            {
                start.ArgumentList.Add(argument);
            }

            Process = Process.Start(start)!;
            _toUpdater.DisposeLocalCopyOfClientHandle();
            _fromUpdater.DisposeLocalCopyOfClientHandle();
            Reader = new StreamReader(_fromUpdater);
            Writer = new StreamWriter(_toUpdater) { AutoFlush = true };
        }

        public Process Process { get; }

        public StreamReader Reader { get; }

        public StreamWriter Writer { get; }

        public string? ReadLine()
        {
            var line = Task.Run(Reader.ReadLine);
            Assert.True(line.Wait(TimeSpan.FromSeconds(20)), "the updater answered");
            return line.Result;
        }

        public int Exit()
        {
            Assert.True(Process.WaitForExit(20_000), "the updater exited");
            return Process.ExitCode;
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill();
            }

            Process.Dispose();
            Writer.Dispose();
            Reader.Dispose();
        }
    }

    private IEnumerable<string> InstallArguments(Process target, string pipeIn, string pipeOut, long? start = null) =>
    [
        "install", "--protocol", "1", "--install-dir", _sandbox.Installation, "--updates-root", _sandbox.Updates,
        "--version", "0.3.0", "--from-version", "0.2.0", "--pid", target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--pid-start", (start ?? target.StartTime.ToUniversalTime().Ticks).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--pipe-in", pipeIn, "--pipe-out", pipeOut
    ];

    [Fact]
    public void TheUpdaterSaysReadyForItsYatAndLeavesOnCancel()
    {
        var yat = Waiting(FakeYat());
        var before = Snapshot(_sandbox.Installation);
        using var updater = new UpdaterProcess((pipeIn, pipeOut) => InstallArguments(yat, pipeIn, pipeOut));

        Assert.Equal("ready", updater.ReadLine());

        // While it waits for the answer, a second updater for the same installation is refused.
        using (var second = new UpdaterProcess((pipeIn, pipeOut) => InstallArguments(yat, pipeIn, pipeOut)))
        {
            Assert.Equal("error Another update of this YAT is in progress.", second.ReadLine());
            Assert.Equal(ExitCodes.Refused, second.Exit());
        }

        updater.Writer.WriteLine("cancel");
        Assert.Equal(ExitCodes.Done, updater.Exit());
        Assert.False(yat.HasExited);
        Assert.Equal(before, Snapshot(_sandbox.Installation));
        Assert.False(File.Exists(ReleaseInstallation.LockPath(_sandbox.Installation)));
        Assert.Contains("YAT answered: Cancel.", File.ReadAllText(ReleaseInstallation.LogPath(_sandbox.Installation)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheUpdaterLeavesWhenYatGoesAwayWithoutAnswering()
    {
        var yat = Waiting(FakeYat());
        var before = Snapshot(_sandbox.Installation);
        var updater = new UpdaterProcess((pipeIn, pipeOut) => InstallArguments(yat, pipeIn, pipeOut));

        Assert.Equal("ready", updater.ReadLine());
        updater.Writer.Dispose();

        Assert.Equal(ExitCodes.Done, updater.Exit());
        Assert.Equal(before, Snapshot(_sandbox.Installation));
        updater.Dispose();
    }

    [Fact]
    public void TheUpdaterRefusesAProcessThatIsNotItsYat()
    {
        var yat = Waiting(FakeYat());
        using var updater = new UpdaterProcess((pipeIn, pipeOut) => InstallArguments(yat, pipeIn, pipeOut, start: 1));

        Assert.StartsWith("error Process", updater.ReadLine(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Refused, updater.Exit());
    }

    [Fact]
    public void TheUpdaterRefusesArgumentsItDoesNotKnow()
    {
        var yat = Waiting(FakeYat());
        using var updater = new UpdaterProcess((pipeIn, pipeOut) => InstallArguments(yat, pipeIn, pipeOut).Select(argument => argument == "1" ? "2" : argument));

        Assert.StartsWith("error Protocol 2 is not supported", updater.ReadLine(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, updater.Exit());
    }
}
