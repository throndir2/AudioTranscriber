using System.Text;

namespace AudioTranscriber.Storage;

// A continuously refreshed plain-text mirror of a session transcript that other
// applications (for example VS Code) can open and read while it is being written.
public static class LiveTranscriptFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string Render(LibraryStore store, Guid sessionId) => Render(store, sessionId, out _);

    public static string Render(LibraryStore store, Guid sessionId, out int rowCount)
    {
        var session = store.GetSession(sessionId);
        var text = new StringBuilder();
        text.AppendLine(session.Name).Append("Source language: ").AppendLine(session.Language).AppendLine();
        rowCount = 0;
        foreach (var row in store.EnumerateTranscript(sessionId))
        {
            text.AppendLine(TranscriptExporter.TextLine(row));
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
