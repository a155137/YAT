using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace YAT.Updater;

// The YAT process the updater waits for (Task #051.C), identified by more than its id: its start time and the executable
// it runs must be the ones YAT gave. The process is held open from the moment it is identified, so its id can never be
// reused by another process while the updater waits for it to exit.
internal sealed class TargetProcess : IDisposable
{
    private readonly Process _process;

    private TargetProcess(Process process) => _process = process;

    public int Id => _process.Id;

    // The running process with this id, started at this time (UTC ticks), running this executable - or UpdaterException.
    public static TargetProcess Open(int processId, long startUtcTicks, string expectedExecutable)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            throw new UpdaterException($"YAT (process {processId}) is not running.");
        }

        try
        {
            // Pins the process: from here its id belongs to it until the handle is released.
            _ = process.SafeHandle;
            if (process.HasExited)
            {
                throw new UpdaterException($"YAT (process {processId}) has already exited.");
            }

            if (process.StartTime.ToUniversalTime().Ticks != startUtcTicks)
            {
                throw new UpdaterException($"Process {processId} is not the YAT that started the update (another start time).");
            }

            var executable = process.MainModule?.FileName;
            if (executable is null || !SameFile(executable, expectedExecutable))
            {
                throw new UpdaterException($"Process {processId} does not run {expectedExecutable}.");
            }

            return new TargetProcess(process);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            process.Dispose();
            throw new UpdaterException($"Process {processId} cannot be identified: {exception.Message}", exception);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

    public void Dispose() => _process.Dispose();

    // The processes, other than `except`, that run this executable - by the file they run, not by their name. A process
    // named like it whose executable cannot be read is counted: it could be one.
    public static IReadOnlyList<int> OthersRunning(string executable, int? except = null)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                if (process.Id == except)
                {
                    continue;
                }

                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    if (process.MainModule?.FileName is not { } path || SameFile(path, executable))
                    {
                        found.Add(process.Id);
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    found.Add(process.Id);
                }
            }
        }

        return found;
    }

    // Whether two paths name the same file: the same path, or - for a short (8.3) or otherwise different spelling - the
    // same file on the same volume.
    public static bool SameFile(string first, string second)
    {
        if (string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Identity(first) is { } a && Identity(second) is { } b && a == b;
    }

    private static (uint Volume, uint High, uint Low)? Identity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var information)
                ? (information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public ComFileTime CreationTime;
        public ComFileTime LastAccessTime;
        public ComFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
