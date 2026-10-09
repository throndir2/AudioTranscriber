using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using AudioTranscriber.Application;
using AudioTranscriber.Core;
using AudioTranscriber.Providers;

namespace AudioTranscriber.App;

/// <summary>Starts the local diagnostic log, records crashes, and builds the "Save diagnostics" ZIP users attach to bug reports.</summary>
public static class AppDiagnostics
{
    private static bool started;

    public static string LogDirectory(string dataRoot) => Path.Combine(dataRoot, "logs");

    public static void Start(StartupOptions options)
    {
        if (started) return;
        started = true;
        AppLog.Initialize(LogDirectory(options.DataRoot), options.Mcp switch { McpMode.Headless => "mcp", McpMode.Ui => "mcp-ui", _ => "app" });
        AppLog.Info($"AudioTranscriber {AppVersion()} starting (pid {Environment.ProcessId}, mode {(options.Smoke ? "smoke" : options.Mcp.ToString())}).");
        AppLog.Info(string.Join(" · ", RuntimeFacts()));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error($"Unhandled exception (terminating: {e.IsTerminating}).", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved background task exception.", e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) => AppLog.Error("Unhandled UI exception.", e.Exception);
    }

    public static string AppVersion()
    {
        var tag = new AppUpdater().CurrentTag;
        var assembly = typeof(AppDiagnostics).Assembly.GetName().Version;
        return tag ?? $"development build {assembly}";
    }

    private static IEnumerable<string> RuntimeFacts()
    {
        yield return $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        yield return RuntimeInformation.FrameworkDescription;
        yield return $"{RuntimeInformation.ProcessArchitecture} process";
        yield return $"{Environment.ProcessorCount} logical CPUs";
        yield return $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_048_576:N0} MiB RAM";
        yield return $"culture {System.Globalization.CultureInfo.CurrentCulture.Name}";
    }

    /// <summary>
    /// Writes a ZIP with the recent log files, the updater log, a system/app summary, and the Activity panel (without
    /// transcript lines). No audio, transcripts, library database, keys, or settings files are included.
    /// </summary>
    public static async Task CreateBundleAsync(string zipPath, IAppController controller, IEnumerable<ActivityEntry> activity,
        CancellationToken cancellationToken = default)
    {
        AppLog.Info("Saving a diagnostics ZIP.");
        var summary = await BuildSummaryAsync(controller, cancellationToken);
        var activityText = string.Join(Environment.NewLine, activity
            .Where(entry => entry.Kind != ActivityKind.Transcript)
            .Select(entry => $"{entry.Time:yyyy-MM-dd HH:mm:ss} {(entry.Kind == ActivityKind.Error ? "ERROR" : "INFO ")} {entry.Text}"));
        var logDirectory = AppLog.Directory ?? LogDirectory(controller.Store.RootDirectory);
        await Task.Run(() =>
        {
            var temporary = zipPath + ".partial";
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                AddText(zip, "summary.txt", summary);
                AddText(zip, "activity.txt", activityText.Length == 0 ? "(empty)" : activityText);
                if (Directory.Exists(logDirectory))
                    foreach (var file in Directory.EnumerateFiles(logDirectory, AppLog.FilePrefix + "*.log").Order())
                        AddText(zip, "logs/" + Path.GetFileName(file), ReadShared(file));
                var updateLog = Path.Combine(AppUpdater.UpdateRoot, "update.log");
                if (File.Exists(updateLog)) AddText(zip, "logs/update.log", ReadShared(updateLog));
            }
            File.Move(temporary, zipPath, overwrite: true);
        }, cancellationToken);
    }

    private static async Task<string> BuildSummaryAsync(IAppController controller, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine("AudioTranscriber diagnostics");
        text.AppendLine($"Created: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"Version: {AppVersion()}");
        text.AppendLine($"Install folder: {AppContext.BaseDirectory}");
        text.AppendLine($"Data folder: {controller.Store.RootDirectory}");
        foreach (var fact in RuntimeFacts()) text.AppendLine("System: " + fact);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var gpus = await GpuProbe.QueryNvidiaGpusAsync(timeout.Token);
            if (gpus.Count == 0) text.AppendLine("NVIDIA GPU: none found");
            foreach (var gpu in gpus)
                text.AppendLine($"NVIDIA GPU {gpu.Index}: {gpu.Name}, {gpu.MemoryMiB:N0} MiB, compute {gpu.ComputeCapability}, driver {gpu.DriverVersion}");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { text.AppendLine($"NVIDIA GPU: query failed ({error.GetType().Name})"); }
        text.AppendLine();
        text.AppendLine(Prerequisites.Check().Summary);
        text.AppendLine();
        text.AppendLine($"Parakeet model installed: {controller.ParakeetModelReady}");
        text.AppendLine($"Speaker models installed: {controller.DiarizationModelsReady}");
        text.AppendLine($"Whisper model: {(controller.WhisperModelPath is { } whisper ? Path.GetFileName(whisper) : "not installed")}");
        text.AppendLine($"Parakeet on GPU: {controller.GpuParakeetEnabled?.ToString() ?? "not decided"}");
        text.AppendLine($"GPU status: {controller.LocalGpuStatus ?? "(none)"}");
        text.AppendLine($"Setup status: {controller.SetupStatus ?? "(idle)"}");
        text.AppendLine($"NVIDIA key set: {controller.HasNvidiaKey}");
        text.AppendLine($"Remember voices: {controller.RememberVoices}");
        text.AppendLine($"Phrase pause: {controller.PhrasePauseMilliseconds} ms");
        text.AppendLine($"Recording now: {controller.IsRecording}");
        try
        {
            text.AppendLine($"Output devices: {controller.GetOutputDevices().Count}; microphones: {controller.GetMicrophoneDevices().Count}");
            var sessions = controller.Store.GetSessions(10_000);
            text.AppendLine($"Library sessions: {sessions.Count}");
            foreach (var group in sessions.GroupBy(session => session.State.ToString()).OrderBy(group => group.Key))
                text.AppendLine($"  {group.Key}: {group.Count()}");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { text.AppendLine($"Library/devices: unavailable ({error.GetType().Name}: {error.Message})"); }
        return Redact(text.ToString());
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(Redact(content));
    }

    /// <summary>Replaces the Windows user profile path and account name so shared reports do not reveal them.</summary>
    public static string Redact(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (user.Length >= 3) text = System.Text.RegularExpressions.Regex.Replace(text,
            $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(user)}(?![\p{{L}}\p{{N}}])", "<user>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        OpenFolderProcess(folder);
    }

    /// <summary>Opens a file in its default app (for a .csv table: Excel, LibreOffice or a text editor).</summary>
    public static void OpenFile(string file)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(file) { UseShellExecute = true }
            : new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { file } };
        Process.Start(start)?.Dispose();
    }

    private static void OpenFolderProcess(string folder)
    {
        var command = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open";
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(command, $"\"{folder}\"") { UseShellExecute = true }
            : new ProcessStartInfo(command, folder) { UseShellExecute = false };
        Process.Start(start)?.Dispose();
    }
}
