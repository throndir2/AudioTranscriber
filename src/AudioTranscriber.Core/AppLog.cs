using System.Globalization;
using System.Text;

namespace AudioTranscriber.Core;

public enum LogLevel { Info, Warning, Error }

/// <summary>
/// Local, rolling diagnostic log (one file per day under the library's logs folder). Records app events and errors
/// for troubleshooting; callers must never pass transcript text, audio, or credentials. Logging never throws.
/// </summary>
public static class AppLog
{
    public const string FilePrefix = "audiotranscriber-";
    private const long MaxFileBytes = 10L * 1024 * 1024;
    private const int KeepDays = 14;
    private static readonly Lock Gate = new();
    private static string? directory;
    private static string process = "app";
    private static bool full;

    public static string? Directory => directory;

    public static void Initialize(string logDirectory, string processName = "app")
    {
        lock (Gate)
        {
            directory = Path.GetFullPath(logDirectory);
            process = processName;
            full = false;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                var cutoff = DateTime.Now.Date.AddDays(-KeepDays);
                foreach (var file in System.IO.Directory.EnumerateFiles(directory, FilePrefix + "*.log"))
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string CurrentFile(DateTime now) =>
        Path.Combine(directory ?? "", $"{FilePrefix}{now:yyyyMMdd}.log");

    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message, Exception? error = null) => Write(LogLevel.Warning, message, error);
    public static void Error(string message, Exception? error = null) => Write(LogLevel.Error, message, error);

    public static void Write(LogLevel level, string message, Exception? error)
    {
        if (directory is null) return;
        var now = DateTime.Now;
        var text = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(level switch { LogLevel.Error => " ERROR ", LogLevel.Warning => " WARN  ", _ => " INFO  " })
            .Append('[').Append(process).Append(':').Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(message.ReplaceLineEndings(Environment.NewLine + "    "));
        if (error is not null) text.AppendLine().Append("    ").Append(error.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
        text.AppendLine();
        lock (Gate)
        {
            try
            {
                var path = CurrentFile(now);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    if (full) return;
                    full = true;
                    File.AppendAllText(path, "… log size limit reached for today; further entries are dropped." + Environment.NewLine);
                    return;
                }
                full = false;
                // Other processes (MCP, the updater) may append to the same file; open shared and retry briefly.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        var bytes = Encoding.UTF8.GetBytes(text.ToString());
                        stream.Write(bytes);
                        return;
                    }
                    catch (IOException) when (attempt < 3) { Thread.Sleep(15); }
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
    }
}
