using System.Text;

namespace AudioTranscriber.Storage;

// A continuously refreshed plain-text mirror of a session transcript that other
// applications (for example VS Code) can open and read while it is being written.
public static class LiveTranscriptFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    // The setting is a folder (each session gets its own file named after the session) or one fixed file path.
    public static bool IsFolder(string setting)
    {
        var path = setting.Trim();
        return Directory.Exists(path) || !Path.HasExtension(path) ||
            path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);
    }

    public static string TargetPath(string setting, StoredSession session) =>
        IsFolder(setting) ? Path.Combine(setting.Trim(), FileName(session)) : setting.Trim();

    // The session name plus its start time, so every session gets its own file; default names already carry the time.
    public static string FileName(StoredSession session)
    {
        var created = session.CreatedUtc.ToLocalTime();
        var name = session.Name.Trim();
        if (!name.Contains(created.ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal))
            name = (name + " " + created.ToString("yyyy-MM-dd HH:mm")).Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => c == ':' || invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return (safe.Length == 0 ? "live-transcript" : safe) + ".txt";
    }

    public static string Render(LibraryStore store, Guid sessionId) => Render(store, sessionId, out _);

    public static string Render(LibraryStore store, Guid sessionId, out int rowCount)
    {
        var session = store.GetSession(sessionId);
        var text = new StringBuilder();
        text.AppendLine(session.Name).Append("Source language: ").AppendLine(session.Language).AppendLine();
        rowCount = 0;
        foreach (var line in TranscriptLine.Group(store.EnumerateTranscript(sessionId)))
        {
            text.AppendLine(TranscriptExporter.TextLine(line));
            rowCount++;
        }
        return text.ToString();
    }

    // Rewrites in place without taking an exclusive lock: readers may keep the file open,
    // and a reader holding it open never blocks the next update. Returns the file length on disk.
    public static long Write(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        if (Path.GetDirectoryName(fullPath) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(true);
        return stream.Length;
    }
}
