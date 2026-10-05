namespace AudioTranscriber.Application;

/// <summary>
/// Plans pause-aligned chunk boundaries over 16 kHz mono audio from its loudness per 10 ms frame, the same rule live
/// capture uses (a pause of quiet ends a phrase once the chunk is long enough), so existing audio can be re-cut offline.
/// </summary>
public static class PhraseSplitter
{
    public const int FrameSamples = 160;
    // Matches live capture's pause level (about -42 dBFS).
    public const float QuietRms = 0.008f;

    public static float FrameRms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += (double)sample * sample;
        return (float)(Math.Sqrt(sum / samples.Length) / 32768.0);
    }

    /// <summary>Returns the exclusive end sample of each planned chunk; the last one is <paramref name="totalSamples"/>.</summary>
    public static IReadOnlyList<long> Plan(IReadOnlyList<float> frameRms, long totalSamples, int pauseMilliseconds,
        int minimumMilliseconds, int maximumMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSamples);
        if (totalSamples == 0) return [];
        var frames = frameRms.Count;
        var pause = Math.Max(1, pauseMilliseconds / 10);
        var minimum = Math.Max(1, minimumMilliseconds / 10);
        var maximum = Math.Max(minimum + 1, maximumMilliseconds / 10);
        // Keep a little quiet on each side of speech; a long quiet stretch becomes its own chunk.
        var pad = Math.Min(20, pause / 2);
        var candidates = new List<int>();
        for (var i = 0; i < frames;)
        {
            if (frameRms[i] >= QuietRms) { i++; continue; }
            var j = i;
            while (j < frames && frameRms[j] < QuietRms) j++;
            if (j - i >= pause)
            {
                if (i == 0) candidates.Add(j - pad);
                else if (j == frames) candidates.Add(i + pad);
                else if (j - i >= 2 * pad + pause) { candidates.Add(i + pad); candidates.Add(j - pad); }
                else candidates.Add(i + (j - i) / 2);
            }
            i = j;
        }
        var cuts = new List<int>();
        var start = 0;
        foreach (var cut in candidates)
        {
            if (cut <= start || cut >= frames || cut - start < minimum) continue;
            cuts.Add(cut);
            start = cut;
        }
        // A short tail joins the chunk before it when both fit.
        if (cuts.Count > 0 && frames - cuts[^1] < minimum && frames - (cuts.Count > 1 ? cuts[^2] : 0) <= maximum)
            cuts.RemoveAt(cuts.Count - 1);
        // Speech with no long-enough pause is cut at its quietest moment so no chunk exceeds the maximum.
        var bounded = new List<int>();
        start = 0;
        foreach (var end in cuts.Append(frames))
        {
            while (end - start > maximum)
            {
                var best = start + minimum;
                for (var k = start + minimum; k <= Math.Max(start + minimum, Math.Min(start + maximum, end - minimum)); k++)
                    if (frameRms[k] <= frameRms[best]) best = k;
                bounded.Add(best);
                start = best;
            }
            bounded.Add(end);
            start = end;
        }
        var ends = bounded.Select(frame => Math.Min(totalSamples, (long)frame * FrameSamples)).ToList();
        ends[^1] = totalSamples;
        return ends.Where((end, index) => index == 0 ? end > 0 : end > ends[index - 1]).ToList();
    }
}
