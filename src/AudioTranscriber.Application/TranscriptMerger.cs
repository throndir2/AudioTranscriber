using System.Text;
using AudioTranscriber.Storage;

namespace AudioTranscriber.Application;

public sealed record RelativeRecognizedWord(string Text, long StartMilliseconds, long EndMilliseconds);
public sealed record RelativeRecognizedSegment(string Text, long StartMilliseconds, long EndMilliseconds, string Granularity);

public static class TranscriptMerger
{
    public static IReadOnlyList<SegmentDraft> MergeWords(
        RecognitionWindow window, IEnumerable<RelativeRecognizedWord> words, IReadOnlyList<StoredTurn> turns,
        string provenance)
    {
        var result = new List<SegmentDraft>();
        var text = new StringBuilder();
        long groupStart = 0, groupEnd = 0;
        string? groupSpeaker = null;
        var groupUncertain = false;
        void Flush()
        {
            if (text.Length == 0) return;
            result.Add(new(groupStart, groupEnd, text.ToString(), groupSpeaker, "Word", provenance, groupUncertain));
            text.Clear();
        }
        foreach (var word in words)
        {
            ValidateTimes(window, word.StartMilliseconds, word.EndMilliseconds);
            var start = checked(window.SessionStartTicks + word.StartMilliseconds * TimeSpan.TicksPerMillisecond);
            var end = checked(window.SessionStartTicks + word.EndMilliseconds * TimeSpan.TicksPerMillisecond);
            var midpoint = start + (end - start) / 2;
            var finalZeroDuration = window.IncludeCoreEnd && start == end && midpoint >= window.CoreEndTicks &&
                                    midpoint - window.CoreEndTicks <= TimeSpan.TicksPerMillisecond;
            if (midpoint < window.CoreStartTicks || (midpoint >= window.CoreEndTicks && !finalZeroDuration)) continue;
            var assignment = MatchSpeaker(start, end, turns);
            if (text.Length > 0 &&
                (assignment.Speaker != groupSpeaker || assignment.Uncertain != groupUncertain ||
                 start - groupEnd > TimeSpan.TicksPerSecond || text.Length > 180))
                Flush();
            if (text.Length == 0)
            {
                groupStart = start;
                groupSpeaker = assignment.Speaker;
                groupUncertain = assignment.Uncertain;
            }
            AppendToken(text, word.Text);
            groupEnd = Math.Max(start, end);
            if (word.Text.EndsWith('.') || word.Text.EndsWith('?') || word.Text.EndsWith('!')) Flush();
        }
        Flush();
        return result;
    }

    public static IReadOnlyList<SegmentDraft> MergeSegments(
        RecognitionWindow window, IEnumerable<RelativeRecognizedSegment> segments,
        IReadOnlyList<StoredTurn> turns, string provenance)
    {
        var result = new List<SegmentDraft>();
        foreach (var segment in segments)
        {
            ValidateTimes(window, segment.StartMilliseconds, segment.EndMilliseconds);
            var start = checked(window.SessionStartTicks + segment.StartMilliseconds * TimeSpan.TicksPerMillisecond);
            var end = checked(window.SessionStartTicks + segment.EndMilliseconds * TimeSpan.TicksPerMillisecond);
            if (window.CoreStartSample != 0 || window.SampleCount != window.CoreSampleCount)
                throw new InvalidDataException("Coarse transcript timing cannot safely remove overlapping context.");
            var assignment = MatchSpeaker(start, end, turns);
            result.Add(new(start, end, segment.Text, assignment.Speaker, segment.Granularity, provenance, assignment.Uncertain));
        }
        return result;
    }

    public static (string? Speaker, bool Uncertain) MatchSpeaker(long start, long end, IReadOnlyList<StoredTurn> turns)
    {
        var evidenceEnd = Math.Max(end, start + 1);
        long unknown = 0;
        var bySpeaker = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var turn in turns)
        {
            var covered = Math.Min(evidenceEnd, turn.EndTicks) - Math.Max(start, turn.StartTicks);
            if (covered <= 0) continue;
            if (turn.Overlap || turn.Uncertain || turn.SpeakerId is null) unknown += covered;
            else bySpeaker[turn.SpeakerId] = bySpeaker.GetValueOrDefault(turn.SpeakerId) + covered;
        }
        if (bySpeaker.Count == 0) return (null, true);
        var (speaker, dominant) = bySpeaker.MaxBy(pair => pair.Value);
        var speech = unknown + bySpeaker.Values.Sum();
        // Recognizer segment edges often graze a neighbour's turn by a few frames. Attribute the line when one
        // speaker clearly dominates its detected speech; overlap, unknown or mixed speech keeps it unattributed.
        return dominant >= speech * 0.8 && dominant >= (evidenceEnd - start) * 0.4 ? (speaker, false) : (null, true);
    }

    private static void ValidateTimes(RecognitionWindow window, long startMilliseconds, long endMilliseconds)
    {
        var duration = window.SampleCount * 1000L / 16000;
        if (startMilliseconds < 0 || endMilliseconds < startMilliseconds || endMilliseconds > duration + 1)
            throw new InvalidDataException("Provider timestamps are outside the submitted audio window.");
    }

    private static void AppendToken(StringBuilder text, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var trimmed = token.Trim();
        if (text.Length > 0 && !",.!?;:)]}".Contains(trimmed[0])) text.Append(' ');
        text.Append(trimmed);
    }
}
