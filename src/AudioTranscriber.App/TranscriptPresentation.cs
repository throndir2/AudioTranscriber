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

public sealed record TranscriptItem(TranscriptRow Row, string TrackName)
{
    public string Timestamp => Row.Timestamp;
    public string Speaker => Row.SpeakerName;
    public string Text => Row.Text;
    public string Timing => Row.TimingGranularity;
    public string Attribution => Row.Uncertain ? "Uncertain / overlap" : "No uncertainty flag";
    public string Provenance => Row.Provenance;
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
