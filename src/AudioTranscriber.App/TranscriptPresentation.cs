using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
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
        : Rows.All(row => row.VoiceFilled) ? "Matched to your labels"
        : Rows.All(row => row.Uncertain) ? "Uncertain / overlap"
        : Rows.Any(row => row.Uncertain) ? "Partly uncertain" : "Automatic";
    public string Provenance => Distinct(row => row.Provenance);
    public override string ToString() => $"{Timestamp} {Speaker}: {Text}";

    private string Distinct(Func<TranscriptRow, string> value) =>
        string.Join(" + ", Rows.Select(value).Distinct(StringComparer.Ordinal));
}

// The whole transcript as displayed lines. A live refresh keeps the unchanged lines and only touches the changed tail,
// so the grid keeps its scroll position and selection; large changes are applied as one reset.
public sealed class TranscriptCollection : ObservableCollection<TranscriptItem>
{
    private const int IncrementalLimit = 64;

    public void Update(IReadOnlyList<TranscriptItem> items)
    {
        var same = 0;
        while (same < Count && same < items.Count && Same(this[same], items[same])) same++;
        if (Count - same + items.Count - same <= IncrementalLimit)
        {
            while (Count > same) RemoveAt(Count - 1);
            for (var index = same; index < items.Count; index++) Add(items[index]);
            return;
        }
        CheckReentrancy();
        while (Items.Count > same) Items.RemoveAt(Items.Count - 1);
        for (var index = same; index < items.Count; index++) Items.Add(items[index]);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private static bool Same(TranscriptItem current, TranscriptItem next) =>
        current.TrackName == next.TrackName && current.Rows.SequenceEqual(next.Rows);
}

public static class TranscriptPresentation
{
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

    private static readonly Regex LineTimes = new(@"^\[\d+:\d{2}:\d{2}\.\d{3} - \d+:\d{2}:\d{2}\.\d{3}\] ", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>A rendered transcript without the "[start - end] " time prefix on each line, so only "Speaker: text" remains.</summary>
    public static string WithoutTimestamps(string transcript) => LineTimes.Replace(transcript, "");
}
