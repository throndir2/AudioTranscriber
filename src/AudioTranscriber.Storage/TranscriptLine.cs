using System.Text;

namespace AudioTranscriber.Storage;

// Recognition splits one person's long utterance at sentence ends, short pauses and audio chunk edges.
// A line joins consecutive rows from the same speaker on the same track so it reads as one passage, as long as
// nobody else spoke in between and the pause stays under a minute; the stored rows stay separate so speaker analysis,
// labeling and corrections keep their own timing. Unknown rows never join (they may be different people).
public sealed class TranscriptLine
{
    public static readonly long MaxPauseTicks = 60 * TimeSpan.TicksPerSecond;
    public const int MaxLength = 1500;

    private readonly List<TranscriptRow> rows = [];
    private readonly List<(int Start, int Length)> spans = [];
    private readonly StringBuilder text = new();
    private readonly StringBuilder raw = new();

    public TranscriptLine(TranscriptRow row) => Add(row);

    public IReadOnlyList<TranscriptRow> Rows => rows;
    public TranscriptRow First => rows[0];
    public TranscriptRow Last => rows[^1];
    public long StartTicks => rows[0].StartTicks;
    public long EndTicks { get; private set; }
    public string Text => text.ToString();
    public string RawText => raw.ToString();

    public bool CanAppend(TranscriptRow row) =>
        row.TrackId == First.TrackId && SameSpeaker(row, First) &&
        row.StartTicks - EndTicks <= MaxPauseTicks && text.Length + row.Text.Trim().Length < MaxLength;

    private static bool SameSpeaker(TranscriptRow a, TranscriptRow b) => a.SpeakerId is not null
        ? a.SpeakerId == b.SpeakerId
        : b.SpeakerId is null && a.SpeakerName == LibraryStore.MicrophoneDefaultName && b.SpeakerName == LibraryStore.MicrophoneDefaultName;

    public static IEnumerable<TranscriptLine> Group(IEnumerable<TranscriptRow> rows)
    {
        TranscriptLine? line = null;
        foreach (var row in rows)
        {
            if (line is not null && line.CanAppend(row)) { line.Append(row); continue; }
            if (line is not null) yield return line;
            line = new(row);
        }
        if (line is not null) yield return line;
    }

    public void Append(TranscriptRow row)
    {
        if (!CanAppend(row)) throw new InvalidOperationException("The row does not continue this line.");
        Add(row);
    }

    /// <summary>
    /// Maps an edit of the whole line back onto its rows. Only the rows whose text the edit touches change:
    /// the first of them takes the edited passage and the rest become empty.
    /// </summary>
    public IReadOnlyList<(string Id, string Correction)> Corrections(string edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        var current = Text;
        if (current == edited) return [];
        if (rows.Count == 1) return [(First.Id, edited)];
        var prefix = 0;
        while (prefix < current.Length && prefix < edited.Length && current[prefix] == edited[prefix]) prefix++;
        var suffix = 0;
        while (suffix < current.Length - prefix && suffix < edited.Length - prefix &&
               current[^(suffix + 1)] == edited[^(suffix + 1)]) suffix++;
        var changeEnd = current.Length - suffix;
        int first = -1, last = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            var (start, length) = spans[index];
            if (length == 0) continue;
            var touches = prefix == changeEnd ? start <= prefix && prefix <= start + length : start < changeEnd && start + length > prefix;
            if (!touches) continue;
            if (first < 0) first = index;
            last = index;
            if (prefix == changeEnd) break;
        }
        if (first < 0)
        {
            // The edit only changed separators between rows; keep it on the nearest row before it.
            first = last = Math.Max(0, spans.FindLastIndex(span => span.Length > 0 && span.Start <= prefix));
        }
        var from = Math.Min(spans[first].Start, prefix);
        var to = Math.Max(spans[last].Start + spans[last].Length, changeEnd) + edited.Length - current.Length;
        var result = new List<(string, string)> { (rows[first].Id, edited[from..Math.Max(from, to)].Trim()) };
        for (var index = first + 1; index <= last; index++) result.Add((rows[index].Id, ""));
        return result;
    }

    private void Add(TranscriptRow row)
    {
        rows.Add(row);
        EndTicks = rows.Count == 1 ? row.EndTicks : Math.Max(EndTicks, row.EndTicks);
        spans.Add(AppendText(text, row.Text));
        AppendText(raw, row.RawText);
    }

    private static (int Start, int Length) AppendText(StringBuilder target, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return (target.Length, 0);
        if (target.Length > 0 && !",.!?;:)]}".Contains(trimmed[0])) target.Append(' ');
        var start = target.Length;
        target.Append(trimmed);
        return (start, trimmed.Length);
    }
}
