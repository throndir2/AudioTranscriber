using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AudioTranscriber.Audio;

public sealed record MediaTools(string FFmpeg = "ffmpeg", string FFprobe = "ffprobe");

/// <summary>
/// Resolves bare tool names ("ffmpeg"/"ffprobe") to a concrete executable: the copy bundled in the release's
/// ffmpeg folder first, then PATH (including the current registry PATH, which a long-running Explorer may not
/// have picked up yet), then common package-manager locations such as WinGet.
/// </summary>
public static class MediaToolLocator
{
    public const string BundledFolder = "ffmpeg";

    public static string Resolve(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !IsBareName(executable)) return executable;
        return TryFind(executable) ?? throw new FileNotFoundException(MissingMessage(executable), ExecutableName(executable));
    }

    public static string? TryFind(string name)
    {
        var file = ExecutableName(name);
        foreach (var directory in CandidateDirectories())
        {
            try
            {
                var path = Path.Combine(directory, file);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return null;
    }

    public static string MissingMessage(string name) => OperatingSystem.IsWindows()
        ? $"{ExecutableName(name)} was not found. Release packages include it in the '{BundledFolder}' folder next to " +
          "AudioTranscriber.App.exe; re-extract the complete ZIP, or install FFmpeg (for example: winget install Gyan.FFmpeg) and restart the app."
        : $"{name} was not found. Release packages include it in the '{BundledFolder}' folder next to AudioTranscriber.App; " +
          "reinstall the package, or install FFmpeg (for example: sudo apt install ffmpeg) and restart the app.";

    private static bool IsBareName(string executable) =>
        executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) < 0;

    private static string ExecutableName(string name) =>
        OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;

    private static IEnumerable<string> CandidateDirectories()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, BundledFolder);
        yield return baseDirectory;
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? path;
            try { path = Environment.GetEnvironmentVariable("PATH", target); }
            catch (Exception error) when (error is System.Security.SecurityException or NotSupportedException) { continue; }
            if (string.IsNullOrEmpty(path)) continue;
            foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = Environment.ExpandEnvironmentVariables(entry.Trim('"'));
                if (Path.IsPathFullyQualified(expanded)) yield return expanded;
            }
        }
        if (!OperatingSystem.IsWindows()) yield break;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        string[] wingetBins = [];
        try
        {
            if (Directory.Exists(packages))
                wingetBins = Directory.GetDirectories(packages, "*FFmpeg*")
                    .SelectMany(package => Directory.GetDirectories(package))
                    .Select(build => Path.Combine(build, "bin")).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        foreach (var bin in wingetBins) yield return bin;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
        yield return @"C:\ffmpeg\bin";
    }
}

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
        var info = new ProcessStartInfo(MediaToolLocator.Resolve(executable))
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
