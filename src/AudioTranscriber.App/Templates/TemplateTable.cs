using System.Globalization;
using System.Text;

namespace AudioTranscriber.App.Templates;

/// <summary>
/// The template list as a CSV table, one row per template, so it can be edited in Excel, LibreOffice, Google Sheets or a
/// text editor. The app writes the table when the list changes and reads it back when it changes on disk.
/// </summary>
public static class TemplateTable
{
    public const string FileName = "templates.csv";

    public static readonly IReadOnlyList<string> Columns =
    [
        "Folder", "Name", "Favorite", "Prompt", "Auto update", "Every seconds", "Transcript", "Timestamps", "Reference files", "Previous output", "Screenshot",
        "Inputs", "Connection", "Transcript characters", "Write to file", "Output file", "Keep versions", "Max versions", "Id"
    ];

    /// <summary>The list separator Excel uses on this PC, so a double-click opens the table in columns.</summary>
    public static char DefaultSeparator => CultureInfo.CurrentCulture.TextInfo.ListSeparator.Trim() == ";" ? ';' : ',';

    public static string Write(IReadOnlyCollection<OutputTemplate> templates, IReadOnlyCollection<LlmConnection> connections, char separator)
    {
        var text = new StringBuilder();
        Line(text, Columns, separator);
        foreach (var t in templates)
        {
            // Inputs are listed by name; a name used by more than one template is written as its ID instead.
            var inputs = TemplateGraph.Inputs(t, templates).Select(i =>
                templates.Count(o => o.Name.Equals(i.Name, StringComparison.OrdinalIgnoreCase)) == 1 && !i.Name.Contains(';') ? i.Name : i.Id.ToString());
            Line(text,
            [
                t.Folder, t.Name, YesNo(t.IsFavorite), t.Prompt, YesNo(t.AutoUpdate), Number(t.IntervalSeconds), YesNo(t.UseTranscript),
                YesNo(t.IncludeTimestamps), YesNo(t.UseReferences),
                YesNo(t.IncludePrevious), YesNo(t.UseScreenshot), string.Join("; ", inputs),
                connections.FirstOrDefault(c => c.Id == t.ConnectionId)?.Name ?? "", Number(t.MaxTranscriptChars), YesNo(t.WriteToFile),
                t.OutputPath, YesNo(t.KeepVersions), Number(t.MaxVersions), t.Id.ToString()
            ], separator);
        }
        return text.ToString();
    }

    /// <summary>Reads the table's rows as column name → cell text. Throws <see cref="FormatException"/> without a Name column.</summary>
    public static IReadOnlyList<Dictionary<string, string>> Read(string text, out char separator)
    {
        separator = DetectSeparator(text);
        var lines = Parse(text, separator);
        if (lines.Count == 0) return [];
        var header = lines[0].Select(h => h.Trim()).ToArray();
        if (!header.Contains("Name", StringComparer.OrdinalIgnoreCase))
            throw new FormatException("the first row needs the column titles, including Name.");
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (line.All(string.IsNullOrWhiteSpace)) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length && i < line.Count; i++)
                if (header[i].Length > 0) row[header[i]] = line[i];
            for (var i = line.Count; i < header.Length; i++)
                if (header[i].Length > 0) row.TryAdd(header[i], "");
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Builds the new template list from the table rows, in row order. A row updates the template with its Id (or else its
    /// name); other rows become new templates. Templates without a row are left out. Blank or unreadable cells keep the
    /// current value, except Folder, Prompt, Inputs, Connection and Output file, where a blank cell means "none" (or "Default").
    /// </summary>
    public static List<OutputTemplate> Apply(IReadOnlyList<Dictionary<string, string>> rows, IReadOnlyList<OutputTemplate> current,
        IReadOnlyCollection<LlmConnection> connections)
    {
        var matched = new OutputTemplate?[rows.Count];
        var used = new HashSet<OutputTemplate>();
        for (var i = 0; i < rows.Count; i++)
            if (Guid.TryParse(Cell(rows[i], "Id"), out var id) && current.FirstOrDefault(t => t.Id == id && !used.Contains(t)) is { } byId)
                used.Add(matched[i] = byId);
        for (var i = 0; i < rows.Count; i++)
            if (matched[i] is null && Cell(rows[i], "Name")?.Trim() is { Length: > 0 } name &&
                current.FirstOrDefault(t => !used.Contains(t) && t.Name.Equals(name, StringComparison.Ordinal)) is { } byName)
                used.Add(matched[i] = byName);

        var result = new List<OutputTemplate>();
        var ids = new HashSet<Guid>(current.Select(t => t.Id));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var template = matched[i];
            if (template is null)
            {
                template = new OutputTemplate { Name = "New template" };
                if (Guid.TryParse(Cell(row, "Id"), out var id) && id != Guid.Empty && !ids.Contains(id)) template.Id = id;
            }
            ids.Add(template.Id);
            if (Cell(row, "Folder") is { } folder) template.Folder = folder;
            if (Cell(row, "Name")?.Trim() is { Length: > 0 } name) template.Name = name;
            if (Bool(row, "Favorite") is { } favorite) template.IsFavorite = favorite;
            if (Cell(row, "Prompt") is { } prompt) template.Prompt = prompt.Replace("\r\n", "\n").Trim();
            if (Bool(row, "Auto update") is { } auto) template.AutoUpdate = auto;
            if (Int(row, "Every seconds") is { } seconds) template.IntervalSeconds = seconds;
            if (Bool(row, "Transcript") is { } transcript) template.UseTranscript = transcript;
            if (Bool(row, "Timestamps") is { } timestamps) template.IncludeTimestamps = timestamps;
            if (Bool(row, "Reference files") is { } references) template.UseReferences = references;
            if (Bool(row, "Previous output") is { } previous) template.IncludePrevious = previous;
            if (Bool(row, "Screenshot") is { } screenshot) template.UseScreenshot = screenshot;
            // A blank (or "Default") connection follows the connection marked Default; an unknown name keeps the current one.
            if (Cell(row, "Connection")?.Trim() is { } connection)
            {
                if (connection.Length == 0 || connection.Equals("Default", StringComparison.OrdinalIgnoreCase)) template.ConnectionId = null;
                else if (connections.FirstOrDefault(c => c.Name.Equals(connection, StringComparison.OrdinalIgnoreCase)) is { } match) template.ConnectionId = match.Id;
            }
            if (Int(row, "Transcript characters") is { } chars) template.MaxTranscriptChars = chars;
            if (Bool(row, "Write to file") is { } write) template.WriteToFile = write;
            if (Cell(row, "Output file") is { } output) template.OutputPath = output.Trim();
            if (Bool(row, "Keep versions") is { } keep) template.KeepVersions = keep;
            if (Int(row, "Max versions") is { } max) template.MaxVersions = max;
            result.Add(template);
        }

        // Inputs refer to other rows by name (or ID), so they are resolved once every row has its template.
        for (var i = 0; i < rows.Count; i++)
        {
            if (Cell(rows[i], "Inputs") is not { } inputs) continue;
            var template = result[i];
            var resolved = inputs.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => Guid.TryParse(entry, out var id)
                    ? result.FirstOrDefault(t => t.Id == id)
                    : result.FirstOrDefault(t => t != template && t.Name.Equals(entry, StringComparison.OrdinalIgnoreCase)))
                .OfType<OutputTemplate>().Where(t => t != template).Select(t => t.Id).Distinct().ToList();
            if (!resolved.SequenceEqual(template.InputTemplateIds)) template.InputTemplateIds = resolved;
        }
        return result;
    }

    /// <summary>Decodes the file as UTF-8 (with or without BOM) or UTF-16, or as Latin-1 when Excel saved it in an ANSI code page.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static string? Cell(Dictionary<string, string> row, string column) => row.TryGetValue(column, out var value) ? value : null;

    private static bool? Bool(Dictionary<string, string> row, string column) =>
        Cell(row, column)?.Trim().ToLowerInvariant() switch
        {
            "yes" or "y" or "true" or "1" or "x" or "on" or "✓" or "✔" => true,
            "no" or "n" or "false" or "0" or "off" => false,
            _ => null
        };

    private static int? Int(Dictionary<string, string> row, string column)
    {
        var text = Cell(row, column)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)) return value;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) && Math.Abs(number) < int.MaxValue ? (int)Math.Round(number) : null;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Line(StringBuilder text, IEnumerable<string> cells, char separator)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) text.Append(separator);
            first = false;
            var value = cell ?? "";
            if (value.IndexOfAny([separator, '"', '\r', '\n']) >= 0 || value != value.Trim())
                text.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
            else text.Append(value);
        }
        text.Append("\r\n");
    }

    private static char DetectSeparator(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? text : text[..end];
        var counts = new[] { ',', ';', '\t' }.Select(c => (Char: c, Count: header.Count(h => h == c))).OrderByDescending(c => c.Count).First();
        return counts.Count > 0 ? counts.Char : ',';
    }

    private static List<List<string>> Parse(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
