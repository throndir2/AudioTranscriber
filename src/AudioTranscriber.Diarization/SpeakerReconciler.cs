namespace AudioTranscriber.Diarization;

public sealed record LocalSpeakerTurn(double StartSeconds, double EndSeconds, int LocalSpeaker);
public sealed record SpeakerEvidence(int LocalSpeaker, float[] Embedding, double CleanSpeechSeconds,
    double? EvidenceStartSeconds = null, double? EvidenceEndSeconds = null);
public sealed record SpeakerMatch(int LocalSpeaker, string? SpeakerId, double? Similarity, string Quality);

public sealed record SpeakerMatchingOptions(
    double MatchThreshold = 0.70,
    double NewSpeakerThreshold = 0.45,
    double RunnerUpMargin = 0.08,
    double MinimumCleanSpeechSeconds = 2.0)
{
    public void Validate()
    {
        if (!double.IsFinite(MatchThreshold) || MatchThreshold is < 0 or > 1 ||
            !double.IsFinite(NewSpeakerThreshold) || NewSpeakerThreshold is < -1 or > 1 ||
            NewSpeakerThreshold >= MatchThreshold || !double.IsFinite(RunnerUpMargin) ||
            RunnerUpMargin is <= 0 or > 1 || !double.IsFinite(MinimumCleanSpeechSeconds) ||
            MinimumCleanSpeechSeconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(SpeakerMatchingOptions));
    }
}

public sealed class SpeakerReconciler(SpeakerMatchingOptions? options = null)
{
    private readonly SpeakerMatchingOptions options = options ?? new();

    public IReadOnlyList<SpeakerMatch> Reconcile(SpeakerRegistry registry,
        IReadOnlyList<LocalSpeakerTurn> turns, IReadOnlyList<SpeakerEvidence> evidence,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        registry.Validate();
        if (turns.Count > 10_000 || evidence.Count > 256 ||
            turns.Any(t => !double.IsFinite(t.StartSeconds) || !double.IsFinite(t.EndSeconds) ||
                           t.StartSeconds < 0 || t.EndSeconds <= t.StartSeconds || t.LocalSpeaker < 0))
            throw new InvalidDataException("Invalid local speaker evidence.");
        var localIds = turns.Select(t => t.LocalSpeaker).Distinct().ToHashSet();
        if (evidence.Select(e => e.LocalSpeaker).Distinct().Count() != evidence.Count ||
            evidence.Any(e => !localIds.Contains(e.LocalSpeaker)))
            throw new InvalidDataException("Duplicate or unreferenced local speaker evidence.");
        var incompatible = new HashSet<(int, int)>();
        for (var i = 0; i < turns.Count; i++)
        for (var j = i + 1; j < turns.Count; j++)
        {
            if (turns[i].LocalSpeaker != turns[j].LocalSpeaker &&
                Math.Min(turns[i].EndSeconds, turns[j].EndSeconds) -
                Math.Max(turns[i].StartSeconds, turns[j].StartSeconds) > 0.01)
            {
                incompatible.Add((turns[i].LocalSpeaker, turns[j].LocalSpeaker));
                incompatible.Add((turns[j].LocalSpeaker, turns[i].LocalSpeaker));
            }
        }
        var result = new Dictionary<int, SpeakerMatch>();
        foreach (var item in evidence.OrderByDescending(e => e.CleanSpeechSeconds).ThenBy(e => e.LocalSpeaker))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(item.CleanSpeechSeconds) || item.CleanSpeechSeconds < options.MinimumCleanSpeechSeconds)
            {
                result[item.LocalSpeaker] = new(item.LocalSpeaker, null, null, "InsufficientCleanSpeech");
                continue;
            }
            var vector = Embeddings.Normalize(item.Embedding);
            var mergedTargets = registry.Aliases.Keys.Select(registry.ResolveAlias).ToHashSet();
            var ranked = registry.Speakers.Select(p => (Profile: p, Score:
                    mergedTargets.Contains(p.SpeakerId)
                        ? p.Representatives.Max(r => Embeddings.Cosine(r, vector))
                        : (Embeddings.Cosine(p.Centroid, vector) + p.Representatives.Max(r => Embeddings.Cosine(r, vector))) / 2))
                .OrderByDescending(p => p.Score).ThenBy(p => p.Profile.SpeakerId, StringComparer.Ordinal).ToArray();
            var best = ranked.FirstOrDefault();
            var runnerUp = ranked.Length > 1 ? ranked[1].Score : -1;
            if (ranked.Length > 0 && best.Score >= options.MatchThreshold && best.Score - runnerUp >= options.RunnerUpMargin)
            {
                var id = best.Profile.SpeakerId;
                if (result.Values.Any(m => m.SpeakerId == id && incompatible.Contains((m.LocalSpeaker, item.LocalSpeaker))))
                {
                    result[item.LocalSpeaker] = new(item.LocalSpeaker, null, best.Score, "IncompatibleOverlap");
                    continue;
                }
                var profile = best.Profile;
                var weight = Math.Min(profile.ObservationCount, 20);
                registry.Speakers[registry.Speakers.IndexOf(profile)] = profile with
                {
                    Centroid = Embeddings.Normalize(profile.Centroid.Zip(vector, (a, b) => a * weight + b).ToArray()),
                    Representatives = Embeddings.SelectRepresentatives(profile.Representatives.Append(vector)),
                    ObservationCount = Math.Min(1_000_000, profile.ObservationCount + 1)
                };
                registry.Revision++;
                result[item.LocalSpeaker] = new(item.LocalSpeaker, id, best.Score, "Matched");
            }
            else if ((ranked.Length == 0 || best.Score < options.NewSpeakerThreshold) &&
                     registry.Speakers.Count + registry.Aliases.Count < SpeakerRegistry.MaximumSpeakers &&
                     registry.NextSpeakerNumber < 1_000_000)
            {
                var id = $"Speaker{registry.NextSpeakerNumber++}";
                registry.Speakers.Add(new(id, vector, [vector.ToArray()], 1));
                registry.Revision++;
                result[item.LocalSpeaker] = new(item.LocalSpeaker, id, ranked.Length == 0 ? null : best.Score, "NewSpeaker");
            }
            else
            {
                result[item.LocalSpeaker] = new(item.LocalSpeaker, null, ranked.Length == 0 ? null : best.Score, "AmbiguousIdentity");
            }
        }
        foreach (var id in localIds)
            result.TryAdd(id, new(id, null, null, "InsufficientCleanSpeech"));
        return result.Values.OrderBy(m => m.LocalSpeaker).ToArray();
    }
}
