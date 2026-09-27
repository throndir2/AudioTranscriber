using System.Globalization;
using AudioTranscriber.Storage;

namespace AudioTranscriber.App;

public sealed record SpeakerChoice(string? Id, string Name);

public enum ActivityKind { Info, Transcript, Error }

public sealed record ActivityEntry(DateTime Time, ActivityKind Kind, string Text, string? Group = null)
{
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public override string ToString() => $"{TimeText}  {Text}";
}

// One displayed transcript line: consecutive rows from the same speaker are shown merged (see TranscriptLine).
public sealed class TranscriptItem(TranscriptLine line, string trackName)
{
    public TranscriptItem(TranscriptRow row, string trackName) : this(new TranscriptLine(row), trackName) { }

    public TranscriptLine Line { get; } = line;
    public string TrackName { get; } = trackName;
    public TranscriptRow Row => Line.First;
    public IReadOnlyList<TranscriptRow> Rows => Line.Rows;
    public long EndTicks => Line.EndTicks;
    public string Timestamp => Row.Timestamp;
    public string Speaker => Row.SpeakerName;
    public string Text => Line.Text;
    public string RawText => Line.RawText;
    public bool HasCorrection => Rows.Any(row => row.Correction is not null);
    public bool Contains(string rowId) => Rows.Any(row => row.Id == rowId);
    public string Timing => Distinct(row => row.TimingGranularity);
    public string Attribution => Rows.All(row => row.ManualSpeaker) ? "Set by you"
        : Rows.Any(row => row.ManualSpeaker) ? "Partly set by you"
        : Row.Uncertain ? "Uncertain / overlap" : "Automatic";
    public string Provenance => Distinct(row => row.Provenance);
    public override string ToString() => $"{Timestamp} {Speaker}: {Text}";

    private string Distinct(Func<TranscriptRow, string> value) =>
        string.Join(" + ", Rows.Select(value).Distinct(StringComparer.Ordinal));
}

public static class TranscriptPresentation
{
    public const int PageSize = 200;

    public static bool TryTimestamp(string text, out long ticks)
    {
        ticks = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        if (!decimal.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0 || (parts.Length > 1 && seconds >= 60)) return false;
        long minutes = 0, hours = 0;
        if (parts.Length > 1 && (!long.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) ||
            minutes < 0 || (parts.Length == 3 && minutes >= 60))) return false;
        if (parts.Length == 3 && (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < 0))
            return false;
        try
        {
            ticks = checked((long)((hours * 3600m + minutes * 60m + seconds) * TimeSpan.TicksPerSecond));
            return true;
        }
        catch (OverflowException) { return false; }
    }

    public static string Duration(long ticks)
    {
        var span = TimeSpan.FromTicks(Math.Max(0, ticks));
        return $"{(long)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }
}
