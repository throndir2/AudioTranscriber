using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AudioTranscriber.Audio;

public sealed record MediaTools(string FFmpeg = "ffmpeg", string FFprobe = "ffprobe");

/// <summary>An owned, bounded-diagnostic process; disposing it kills its entire process tree.</summary>
public sealed class OwnedMediaProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stderrTask;
    private readonly StringBuilder stderr = new();
    private readonly SafeFileHandle? job;
    private readonly CancellationTokenRegistration cancellation;
    private const int DiagnosticLimit = 32 * 1024;
    private bool disposed;

    public Stream Input => process.StandardInput.BaseStream;
    public Stream Output => process.StandardOutput.BaseStream;
    public int ProcessId => process.Id;
    public string Diagnostics { get { lock (stderr) return stderr.ToString(); } }

    public OwnedMediaProcess(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException($"Could not start {executable}.");
            job = ProcessJob.Attach(process);
            stderrTask = ReadDiagnosticsAsync();
            cancellation = cancellationToken.Register(Kill);
        }
        catch
        {
            Kill();
            process.Dispose();
            throw;
        }
    }

    private async Task ReadDiagnosticsAsync()
    {
        var buffer = new char[2048];
        int count;
        while ((count = await process.StandardError.ReadAsync(buffer)) > 0)
        {
            lock (stderr)
            {
                stderr.Append(buffer, 0, count);
                if (stderr.Length > DiagnosticLimit) stderr.Remove(0, stderr.Length - DiagnosticLimit);
            }
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await stderrTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new IOException($"Media process exited with code {process.ExitCode}: {Diagnostics}");
        }
        catch { Kill(); throw; }
    }

    public async Task<string> ReadTextAsync(int maxCharacters, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(Output, Encoding.UTF8, leaveOpen: true);
        var text = new StringBuilder();
        var buffer = new char[Math.Min(4096, maxCharacters)];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (text.Length + count > maxCharacters)
            {
                Kill();
                throw new InvalidDataException("Media tool output exceeded its configured bound.");
            }
            text.Append(buffer, 0, count);
        }
        await CompleteAsync(cancellationToken);
        return text.ToString();
    }

    private void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Dispose();
        Kill();
        try { await process.WaitForExitAsync(); await stderrTask; }
        finally { job?.Dispose(); process.Dispose(); }
    }

    private static class ProcessJob
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint Priority, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        public static SafeFileHandle? Attach(Process process)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var handle = CreateJobObject(IntPtr.Zero, null);
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(handle, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new System.ComponentModel.Win32Exception(error, "Unable to establish media-process lifetime ownership.");
            }
            return handle;
        }
    }
}

internal static class LocalMedia
{
    internal const string Formats = "wav,mp3,flac,ogg,mov,matroska,webm,aac,ac3,aiff,asf,avi,mpeg,mpegts,au";

    public static string RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Only absolute local media-file paths are supported.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Media file does not exist.", full);
        if ((File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new ArgumentException("Media must be a regular local file, not a link.", nameof(path));
        return full;
    }

    public static void CheckSpace(string path, long requiredBytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < requiredBytes)
            throw new IOException($"Insufficient free space: at least {requiredBytes:N0} bytes required.");
    }

    public static void AtomicJson<T>(string path, T value)
    {
        var pending = path + ".pending";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            System.Text.Json.JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, path, overwrite: true);
    }
}
