using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AudioTranscriber.App.Templates;

/// <summary>
/// Read-only access to the user's reference files: a context folder the model can browse and search through tools,
/// plus files whose text is always included. Files are opened shared, so other apps can keep editing them.
/// </summary>
public sealed partial class ReferenceLibrary(string? folder, IReadOnlyList<string> pinnedFiles) : ILlmToolHost
{
    private const int ReadChunk = 20000;
    private const int MaxListed = 400;
    private const int MaxMatches = 40;
    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, string Text)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".xml", ".html", ".htm", ".yaml", ".yml", ".ini", ".log",
        ".srt", ".vtt", ".rst", ".org", ".adoc", ".tex", ".js", ".ts", ".cs", ".py", ".lua", ".toml"
    };

    private readonly string? root = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder.Trim());
    private readonly string[] pinned = pinnedFiles.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p.Trim())).ToArray();

    public bool HasFolder => root is not null && Directory.Exists(root);

    public static bool IsReadable(string path) =>
        TextExtensions.Contains(Path.GetExtension(path)) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Text of the always-included files, each under a heading, capped in total size.</summary>
    public string PinnedText(int maxChars)
    {
        var text = new StringBuilder();
        foreach (var path in pinned)
        {
            string body;
            try { body = File.Exists(path) ? Extract(path) : "(file not found)"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { body = "(could not read: " + ex.Message + ")"; }
            catch (Exception ex) { body = "(could not read: " + ex.Message + ")"; }
            var remaining = maxChars - text.Length;
            if (remaining <= 200) { text.AppendLine($"### {Path.GetFileName(path)}").AppendLine("(omitted: reference text limit reached)"); continue; }
            if (body.Length > remaining - 100) body = body[..(remaining - 100)] + "\n[…truncated…]";
            text.AppendLine($"### {Path.GetFileName(path)}").AppendLine(body.Trim()).AppendLine();
        }
        return text.ToString();
    }

    public JsonArray Definitions =>
    [
        Function("list_files", "List the user's reference files (rules, notes, adventure PDFs, lore…) in the context folder. Use before reading.",
            new JsonObject { ["path"] = Prop("string", "Optional subfolder, relative to the context folder.") }),
        Function("read_file", $"Read the text of a reference file (PDF, DOCX, Markdown, text…). Returns up to {ReadChunk} characters starting at 'offset'; call again with a larger offset to continue.",
            new JsonObject { ["path"] = Prop("string", "File path relative to the context folder, as shown by list_files."), ["offset"] = Prop("integer", "Character offset to start at (default 0).") },
            "path"),
        Function("search_files", "Case-insensitive text search across all reference files. Returns matching lines with file, page (for PDFs) and offset so you can read_file around them.",
            new JsonObject { ["query"] = Prop("string", "Words or a phrase to find, for example an NPC or place name.") }, "query")
    ];

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
            }
        }
    };

    public Task<string> InvokeAsync(string name, string arguments, CancellationToken cancellationToken) => Task.Run(() =>
    {
        JsonNode? args = null;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments); }
        catch (JsonException) { }
        return name switch
        {
            "list_files" => List(args?["path"]?.ToString()),
            "read_file" => Read(args?["path"]?.ToString() ?? "", ParseInt(args?["offset"])),
            "search_files" => Search(args?["query"]?.ToString() ?? "", cancellationToken),
            _ => $"Unknown tool '{name}'."
        };
    }, cancellationToken);

    private static int ParseInt(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? Math.Max(0, i) : 0;

    private IEnumerable<string> AllFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in pinned) if (File.Exists(path) && seen.Add(path)) yield return path;
        if (!HasFolder) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        foreach (var path in Directory.EnumerateFiles(root!, "*", options))
            if (IsReadable(path) && seen.Add(path)) yield return path;
    }

    private string Display(string path) =>
        root is not null && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : path;

    // Only files inside the context folder or explicitly pinned are reachable.
    private string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');
        var candidates = new List<string>();
        if (Path.IsPathRooted(path)) candidates.Add(Path.GetFullPath(path));
        else if (root is not null) candidates.Add(Path.GetFullPath(Path.Combine(root, path)));
        foreach (var full in candidates)
        {
            if (pinned.Contains(full, StringComparer.OrdinalIgnoreCase)) return full;
            if (root is not null && (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return full;
        }
        // Accept a bare file name when it is unique.
        var byName = AllFiles().Where(f => Path.GetFileName(f).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return byName.Length == 1 ? byName[0] : null;
    }

    private string List(string? sub)
    {
        var text = new StringBuilder();
        if (string.IsNullOrWhiteSpace(sub))
            foreach (var path in pinned.Where(File.Exists)) text.AppendLine($"{path} (always included, {Size(path)})");
        if (!HasFolder) return text.Length > 0 ? text.ToString() : "No context folder is set and no reference files are included.";
        var directory = string.IsNullOrWhiteSpace(sub) ? root! : Resolve(sub);
        if (directory is null || !Directory.Exists(directory)) return $"Folder '{sub}' was not found in the context folder.";
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", options).Where(IsReadable).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (++count > MaxListed) { text.AppendLine($"…more files not shown; list a subfolder."); break; }
            text.AppendLine($"{Display(path)} ({Size(path)})");
        }
        return text.Length == 0 ? "The context folder has no readable files (PDF, DOCX, TXT, MD…)." : text.ToString();
    }

    private static string Size(string path)
    {
        try { var bytes = new FileInfo(path).Length; return bytes < 1024 ? $"{bytes} bytes" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1048576.0:0.#} MB"; }
        catch (IOException) { return "?"; }
    }

    private string Read(string path, int offset)
    {
        var full = Resolve(path);
        if (full is null || !File.Exists(full)) return $"File '{path}' was not found among the reference files. Use list_files.";
        string text;
        try { text = Extract(full); }
        catch (Exception ex) { return $"Could not read '{path}': {ex.Message}"; }
        if (offset >= text.Length) return $"[{Display(full)}: {text.Length} characters total; offset {offset} is past the end]";
        var length = Math.Min(ReadChunk, text.Length - offset);
        var more = offset + length < text.Length ? $"; call read_file with offset {offset + length} for more" : "; end of file";
        return $"[{Display(full)}: characters {offset}–{offset + length} of {text.Length}{more}]\n" + text.Substring(offset, length);
    }

    private string Search(string query, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length == 0) return "Give a search query.";
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new StringBuilder();
        var matches = 0;
        foreach (var path in AllFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try { text = Extract(path); }
            catch { continue; }
            var lineStart = 0;
            var page = 0;
            while (lineStart < text.Length && matches < MaxMatches)
            {
                var end = text.IndexOf('\n', lineStart);
                if (end < 0) end = text.Length;
                var line = text.Substring(lineStart, end - lineStart);
                if (line.StartsWith("[Page ")) int.TryParse(line.AsSpan(6).TrimEnd(']'), out page);
                else if (line.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         (words.Length > 1 && words.All(w => line.Contains(w, StringComparison.OrdinalIgnoreCase))))
                {
                    var snippet = line.Trim();
                    if (snippet.Length > 240) snippet = snippet[..240] + "…";
                    results.AppendLine($"{Display(path)}{(page > 0 ? $" p.{page}" : "")} @{lineStart}: {snippet}");
                    matches++;
                }
                lineStart = end + 1;
            }
            if (matches >= MaxMatches) { results.AppendLine("…more matches not shown; refine the query."); break; }
        }
        return results.Length == 0 ? $"No matches for '{query}'." : results.ToString();
    }

    public static string Extract(string path)
    {
        var info = new FileInfo(path);
        if (Cache.TryGetValue(path, out var cached) && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length) return cached.Text;
        var bytes = ReadShared(path);
        var text = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => Pdf(bytes),
            ".docx" => Docx(bytes),
            _ => Encoding.UTF8.GetString(bytes).Replace("\0", "")
        };
        text = text.ReplaceLineEndings("\n");
        Cache[path] = (info.LastWriteTimeUtc, info.Length, text);
        return text;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 512L * 1024 * 1024) throw new IOException("the file is larger than 512 MB");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Pdf(byte[] bytes)
    {
        var text = new StringBuilder();
        using var document = PdfDocument.Open(bytes);
        foreach (var page in document.GetPages())
        {
            string pageText;
            try { pageText = ContentOrderTextExtractor.GetText(page); }
            catch { pageText = page.Text; }
            text.Append("[Page ").Append(page.Number).AppendLine("]").AppendLine(pageText.Trim()).AppendLine();
        }
        return text.ToString();
    }

    private static string Docx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("not a Word document");
        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        xml = Paragraph().Replace(xml, "\n");
        xml = Tab().Replace(xml, "\t");
        xml = Tag().Replace(xml, "");
        return System.Net.WebUtility.HtmlDecode(xml);
    }

    [GeneratedRegex(@"</w:p>|<w:br\s*/>")] private static partial Regex Paragraph();
    [GeneratedRegex(@"<w:tab\s*/>")] private static partial Regex Tab();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tag();
}
