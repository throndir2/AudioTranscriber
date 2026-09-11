namespace AudioTranscriber.Diarization;

public static class CleanSpeech
{
    public static IReadOnlyList<LocalSpeakerTurn> Intervals(IReadOnlyList<LocalSpeakerTurn> turns,
        int localSpeaker, double edgeTrimSeconds = 0.12, double minimumSeconds = 1.5)
    {
        var result = new List<LocalSpeakerTurn>();
        var merged = new List<LocalSpeakerTurn>();
        foreach (var turn in turns.Where(t => t.LocalSpeaker == localSpeaker).OrderBy(t => t.StartSeconds))
        {
            if (merged.Count > 0 && merged[^1].EndSeconds >= turn.StartSeconds)
                merged[^1] = merged[^1] with { EndSeconds = Math.Max(merged[^1].EndSeconds, turn.EndSeconds) };
            else merged.Add(turn);
        }
        foreach (var turn in merged)
        {
            var spans = new List<(double Start, double End)> { (turn.StartSeconds, turn.EndSeconds) };
            foreach (var other in turns.Where(t => t.LocalSpeaker != localSpeaker))
            {
                var next = new List<(double, double)>();
                foreach (var (start, end) in spans)
                {
                    if (other.EndSeconds <= start || other.StartSeconds >= end) next.Add((start, end));
                    else
                    {
                        if (other.StartSeconds > start) next.Add((start, Math.Min(end, other.StartSeconds)));
                        if (other.EndSeconds < end) next.Add((Math.Max(start, other.EndSeconds), end));
                    }
                }
                spans = next;
            }
            result.AddRange(spans.Where(s => s.End - s.Start - 2 * edgeTrimSeconds >= minimumSeconds)
                .Select(s => new LocalSpeakerTurn(s.Start + edgeTrimSeconds, s.End - edgeTrimSeconds, localSpeaker)));
        }
        return result.OrderByDescending(t => t.EndSeconds - t.StartSeconds).ThenBy(t => t.StartSeconds).ToArray();
    }

    public static IEnumerable<(double Start, double End, bool Overlap)> SplitAtOverlap(
        LocalSpeakerTurn turn, IReadOnlyList<LocalSpeakerTurn> turns)
    {
        var others = turns.Where(t => t.LocalSpeaker != turn.LocalSpeaker &&
            t.EndSeconds > turn.StartSeconds && t.StartSeconds < turn.EndSeconds).ToArray();
        var points = others.SelectMany(t => new[] { Math.Max(turn.StartSeconds, t.StartSeconds), Math.Min(turn.EndSeconds, t.EndSeconds) })
            .Append(turn.StartSeconds).Append(turn.EndSeconds).Distinct().Order().ToArray();
        for (var i = 1; i < points.Length; i++)
        {
            var start = points[i - 1];
            var end = points[i];
            yield return (start, end, others.Any(t => t.StartSeconds < end && t.EndSeconds > start));
        }
    }
}
