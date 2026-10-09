using AudioTranscriber.Core;

namespace AudioTranscriber.Diarization;

// Cross-session speaker recognition on stored embeddings only: no audio is read or re-analyzed.
public static class VoiceLibrary
{
    // Per session speaker, a few diverse samples; across sessions, the newest samples win.
    public const int MaximumSamplesPerSpeaker = 3;
    public const int MaximumSamples = 15;
    // Measured on AMI meetings (the same people in several sessions): more than half of the true matches across
    // sessions scored below the in-session 0.70; strangers stayed below 0.57. Wrong names came only from session
    // speakers with little speech, so a speaker is compared only after this much clean speech.
    public const double MatchThreshold = 0.60;
    public const double MinimumEvidenceSeconds = 20;
    public static readonly SpeakerMatchingOptions Matching = new(MatchThreshold: MatchThreshold);

    public sealed record Print(float[] Centroid, IReadOnlyList<float[]> Samples);

    // Clean speech the session speaker has been matched on, including speakers merged into it.
    public static double EvidenceSeconds(SpeakerRegistrySnapshot registry, Guid speakerId) =>
        registry.Speakers.Where(e => Resolve(registry, e.Identity.Id) == speakerId).Sum(e => e.EvidenceDurationTicks) /
        (double)TimeSpan.TicksPerSecond;

    // A session speaker's voice: its centroid and representatives, plus representatives of speakers merged into it.
    public static Print? FromSession(SpeakerRegistrySnapshot registry, Guid speakerId)
    {
        var entry = registry.Speakers.FirstOrDefault(e => e.Identity.Id == speakerId && e.Identity.MergedIntoId is null);
        if (entry is null || entry.EmbeddingModelId != DiarizationModels.EmbeddingSha256) return null;
        var samples = registry.Speakers.Where(e => Resolve(registry, e.Identity.Id) == speakerId)
            .SelectMany(e => e.Representatives).Where(r => r.ModelId == DiarizationModels.EmbeddingSha256 && r.Values.Length == Embeddings.Dimension)
            .Select(r => Embeddings.Normalize(r.Values.ToArray())).ToList();
        return samples.Count == 0 ? null : new(Embeddings.Normalize(entry.Centroid.ToArray()), samples);
    }

    public static Print? FromSamples(IEnumerable<float[]> samples)
    {
        var list = samples.Where(v => v is { Length: Embeddings.Dimension } && v.All(float.IsFinite) && v.Any(x => x != 0))
            .Select(Embeddings.Normalize).ToList();
        if (list.Count == 0) return null;
        var sum = new float[Embeddings.Dimension];
        foreach (var vector in list)
            for (var i = 0; i < sum.Length; i++) sum[i] += vector[i];
        return new(Embeddings.Normalize(sum), list);
    }

    // Same score as in-session matching: centroid agreement averaged with the closest pair of samples.
    public static double Score(Print a, Print b) =>
        (Embeddings.Cosine(a.Centroid, b.Centroid) + a.Samples.Max(x => b.Samples.Max(y => Embeddings.Cosine(x, y)))) / 2;

    // The one remembered voice that clears the match threshold and the runner-up margin; otherwise null.
    public static int? BestMatch(Print probe, IReadOnlyList<Print> voices, out double score, SpeakerMatchingOptions? options = null)
    {
        options ??= Matching;
        options.Validate();
        score = 0;
        if (voices.Count == 0) return null;
        var ranked = voices.Select((voice, index) => (Index: index, Score: Score(probe, voice)))
            .OrderByDescending(item => item.Score).ToArray();
        score = ranked[0].Score;
        var runnerUp = ranked.Length > 1 ? ranked[1].Score : -1;
        return score >= options.MatchThreshold && score - runnerUp >= options.RunnerUpMargin ? ranked[0].Index : null;
    }

    public static IReadOnlyList<float[]> SelectSamples(IEnumerable<float[]> samples) =>
        Embeddings.SelectRepresentatives(samples, MaximumSamplesPerSpeaker);

    private static Guid Resolve(SpeakerRegistrySnapshot registry, Guid id)
    {
        for (var hops = 0; hops <= SpeakerRegistry.MaximumSpeakers; hops++)
        {
            var next = registry.Speakers.FirstOrDefault(e => e.Identity.Id == id)?.Identity.MergedIntoId;
            if (next is null) return id;
            id = next.Value;
        }
        return id;
    }
}
