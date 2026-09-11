using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AudioTranscriber.Core;

namespace AudioTranscriber.Diarization;

public static class CoreRegistrySerializer
{
    public static string Serialize(SpeakerRegistrySnapshot snapshot)
    {
        ToState(snapshot);
        var json = JsonSerializer.Serialize(snapshot);
        if (Encoding.UTF8.GetByteCount(json) > SpeakerRegistry.MaximumJsonBytes)
            throw new InvalidDataException("Speaker registry exceeds its storage limit.");
        return json;
    }

    public static SpeakerRegistrySnapshot Deserialize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > SpeakerRegistry.MaximumJsonBytes)
            throw new InvalidDataException("Speaker registry exceeds its storage limit.");
        var result = JsonSerializer.Deserialize<SpeakerRegistrySnapshot>(json)
            ?? throw new InvalidDataException("Speaker registry is missing.");
        ToState(result);
        return result;
    }

    internal static SpeakerRegistry ToState(SpeakerRegistrySnapshot snapshot)
    {
        if (snapshot.SessionId == Guid.Empty || snapshot.Revision < 0 || snapshot.Speakers.IsDefault ||
            snapshot.Speakers.Length > SpeakerRegistry.MaximumSpeakers ||
            snapshot.Speakers.Select(e => e.Identity.Id).Distinct().Count() != snapshot.Speakers.Length ||
            snapshot.Speakers.Select(e => e.Identity.Number).Distinct().Count() != snapshot.Speakers.Length ||
            snapshot.Speakers.Any(e => e.Identity.SessionId != snapshot.SessionId || e.Identity.Id == Guid.Empty ||
                                      e.Identity.Number is < 1 or >= 1_000_000 || e.Identity.Revision < 0 ||
                                      e.EmbeddingModelId != DiarizationModels.EmbeddingSha256 ||
                                      e.EvidenceDurationTicks < 0 || e.Identity.DisplayName.Length > 1024 ||
                                      e.Representatives.IsDefaultOrEmpty ||
                                      e.Representatives.Length > SpeakerRegistry.MaximumRepresentatives))
            throw new InvalidDataException("Unsupported or invalid speaker registry snapshot.");
        var state = new SpeakerRegistry
        {
            NextSpeakerNumber = snapshot.Speakers.Length == 0 ? 1 : snapshot.Speakers.Max(e => e.Identity.Number) + 1,
            Revision = snapshot.Revision
        };
        foreach (var entry in snapshot.Speakers)
        {
            var centroid = entry.Centroid.ToArray();
            Embeddings.Validate(centroid);
            var representatives = entry.Representatives.Select(e =>
            {
                if (e.SpeakerId != entry.Identity.Id || e.SessionId != snapshot.SessionId ||
                    e.ModelId != DiarizationModels.EmbeddingSha256 || e.EvidenceEndTicks < e.EvidenceStartTicks)
                    throw new InvalidDataException("Invalid representative speaker embedding.");
                var vector = e.Values.ToArray();
                Embeddings.Validate(vector);
                return vector;
            }).ToList();
            state.Speakers.Add(new($"Speaker{entry.Identity.Number}", centroid, representatives,
                (int)Math.Clamp(entry.EvidenceDurationTicks / TimeSpan.TicksPerSecond / 2, 1, 1_000_000)));
        }
        foreach (var entry in snapshot.Speakers.Where(e => e.Identity.MergedIntoId is not null))
        {
            var target = snapshot.Speakers.FirstOrDefault(e => e.Identity.Id == entry.Identity.MergedIntoId)
                ?? throw new InvalidDataException("Merged speaker target is missing.");
            state.Aliases[$"Speaker{entry.Identity.Number}"] = $"Speaker{target.Identity.Number}";
        }
        foreach (var alias in state.Aliases.Keys) state.ResolveAlias(alias);
        foreach (var alias in state.Aliases.Keys.ToArray())
        {
            var from = state.Speakers.Single(p => p.SpeakerId == alias);
            var targetId = state.ResolveAlias(alias);
            var into = state.Speakers.Single(p => p.SpeakerId == targetId);
            // A user merge is an explicit instruction; retain both voices as representatives.
            state.Speakers[state.Speakers.IndexOf(into)] = into with
            {
                Representatives = Embeddings.SelectRepresentatives(into.Representatives.Concat(from.Representatives))
            };
            state.Speakers.Remove(from);
        }
        state.Validate();
        return state;
    }

    internal static SpeakerRegistrySnapshot ToSnapshot(SpeakerRegistry state, SpeakerRegistrySnapshot previous,
        DiarizationRequest request, IReadOnlyList<SpeakerEvidence> evidence, IReadOnlyList<SpeakerMatch> matches)
    {
        var entries = new List<SpeakerRegistryEntry>();
        foreach (var profile in state.Speakers)
        {
            var number = int.Parse(profile.SpeakerId.AsSpan(7));
            var old = previous.Speakers.FirstOrDefault(e => e.Identity.Number == number);
            var identity = old?.Identity ?? new SpeakerIdentity(Guid.NewGuid(), request.SessionId, number, $"Speaker {number}");
            var duration = evidence.Where(e => matches.Any(m => m.LocalSpeaker == e.LocalSpeaker && m.SpeakerId == profile.SpeakerId))
                .Sum(e => e.CleanSpeechSeconds);
            var representatives = profile.Representatives.Select(vector =>
            {
                var retained = old?.Representatives.FirstOrDefault(e => e.Values.AsSpan().SequenceEqual(vector));
                if (retained is not null) return retained;
                var merged = previous.Speakers.SelectMany(e => e.Representatives)
                    .FirstOrDefault(e => e.Values.AsSpan().SequenceEqual(vector));
                if (merged is not null) return merged with { Id = Guid.NewGuid(), SpeakerId = identity.Id };
                var observation = evidence.FirstOrDefault(e => Embeddings.Cosine(e.Embedding, vector) > 0.999999 &&
                    matches.Any(m => m.LocalSpeaker == e.LocalSpeaker && m.SpeakerId == profile.SpeakerId));
                var start = observation?.EvidenceStartSeconds ?? 0;
                var end = observation?.EvidenceEndSeconds ?? request.SampleCount / 16000.0;
                return new SpeakerEmbedding(Guid.NewGuid(), request.SessionId, identity.Id, DiarizationModels.EmbeddingSha256,
                    vector.ToImmutableArray(), checked(request.SessionStartTicks + (long)Math.Round(start * TimeSpan.TicksPerSecond)),
                    checked(request.SessionStartTicks + (long)Math.Round(end * TimeSpan.TicksPerSecond)));
            }).ToImmutableArray();
            entries.Add(new(identity, DiarizationModels.EmbeddingSha256, profile.Centroid.ToImmutableArray(),
                representatives, checked((old?.EvidenceDurationTicks ?? 0) + (long)(duration * TimeSpan.TicksPerSecond))));
        }
        // Keep tombstones so explicit merges, old transcript references, and numbering survive reprocessing.
        entries.AddRange(previous.Speakers.Where(e => e.Identity.MergedIntoId is not null));
        return new(request.SessionId, state.Revision, entries.OrderBy(e => e.Identity.Number).ToImmutableArray());
    }
}
