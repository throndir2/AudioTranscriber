using System.Text.Json;

namespace AudioTranscriber.Diarization;

public sealed record SpeakerProfile(string SpeakerId, float[] Centroid, List<float[]> Representatives, int ObservationCount);

public sealed class SpeakerRegistry
{
    public const int MaximumSpeakers = 256;
    public const int MaximumRepresentatives = 5;
    public const int MaximumJsonBytes = 8 * 1024 * 1024;
    public int Version { get; init; } = 1;
    public string EmbeddingModelSha256 { get; init; } = DiarizationModels.EmbeddingSha256;
    public int NextSpeakerNumber { get; set; } = 1;
    public long Revision { get; set; }
    public List<SpeakerProfile> Speakers { get; init; } = [];
    public Dictionary<string, string> Aliases { get; init; } = [];

    public string Serialize()
    {
        Validate();
        var json = JsonSerializer.Serialize(this);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("Speaker registry exceeds its storage limit.");
        return json;
    }

    public static SpeakerRegistry Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SpeakerRegistry();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("Speaker registry exceeds its storage limit.");
        var state = JsonSerializer.Deserialize<SpeakerRegistry>(json)
            ?? throw new InvalidDataException("Speaker registry is missing.");
        state.Validate();
        return state;
    }

    public string ResolveAlias(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (Aliases.TryGetValue(id, out var target))
        {
            if (!seen.Add(id)) throw new InvalidDataException("Speaker alias cycle.");
            id = target;
        }
        return id;
    }

    public void Merge(string fromSpeakerId, string intoSpeakerId)
    {
        Validate();
        fromSpeakerId = ResolveAlias(fromSpeakerId);
        intoSpeakerId = ResolveAlias(intoSpeakerId);
        if (fromSpeakerId == intoSpeakerId) return;
        var from = Speakers.Single(p => p.SpeakerId == fromSpeakerId);
        var into = Speakers.Single(p => p.SpeakerId == intoSpeakerId);
        var representatives = Embeddings.SelectRepresentatives(into.Representatives.Concat(from.Representatives));
        Speakers.Remove(from);
        Speakers[Speakers.IndexOf(into)] = into with
        {
            Centroid = Embeddings.Normalize(into.Centroid.Zip(from.Centroid, (a, b) => a + b).ToArray()),
            Representatives = representatives,
            ObservationCount = Math.Min(1_000_000, into.ObservationCount + from.ObservationCount)
        };
        Aliases[fromSpeakerId] = intoSpeakerId;
        foreach (var alias in Aliases.Keys.ToArray()) Aliases[alias] = ResolveAlias(alias);
        Revision++;
    }

    public void Validate()
    {
        if (Version != 1 || EmbeddingModelSha256 != DiarizationModels.EmbeddingSha256 ||
            NextSpeakerNumber < 1 || NextSpeakerNumber > 1_000_000 || Revision < 0 ||
            Speakers is null || Speakers.Count > MaximumSpeakers || Aliases is null ||
            Speakers.Count + Aliases.Count > MaximumSpeakers)
            throw new InvalidDataException("Unsupported or invalid speaker registry.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in Speakers)
        {
            ValidateId(profile.SpeakerId);
            if (!ids.Add(profile.SpeakerId) || profile.ObservationCount is < 1 or > 1_000_000 ||
                profile.Representatives is null || profile.Representatives.Count is < 1 or > MaximumRepresentatives)
                throw new InvalidDataException("Invalid speaker profile.");
            Embeddings.Validate(profile.Centroid);
            foreach (var vector in profile.Representatives) Embeddings.Validate(vector);
        }
        foreach (var (alias, _) in Aliases)
        {
            ValidateId(alias);
            if (ids.Contains(alias) || !ids.Contains(ResolveAlias(alias)))
                throw new InvalidDataException("Invalid speaker alias.");
        }
    }

    private void ValidateId(string id)
    {
        if (id is null || !id.StartsWith("Speaker", StringComparison.Ordinal) ||
            !int.TryParse(id.AsSpan(7), out var number) || number < 1 || number >= NextSpeakerNumber ||
            id != $"Speaker{number}")
            throw new InvalidDataException("Invalid immutable speaker ID.");
    }
}

internal static class Embeddings
{
    public const int Dimension = 256;
    public static void Validate(float[] vector)
    {
        if (vector is null || vector.Length != Dimension || vector.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid speaker embedding.");
        var norm = vector.Sum(x => (double)x * x);
        if (norm < 0.98 || norm > 1.02) throw new InvalidDataException("Speaker embedding must be normalized.");
    }

    public static float[] Normalize(float[] vector)
    {
        if (vector.Length != Dimension || vector.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid speaker embedding.");
        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        if (norm < 1e-8) throw new InvalidDataException("Empty speaker embedding.");
        return vector.Select(x => (float)(x / norm)).ToArray();
    }

    public static double Cosine(float[] a, float[] b) => Math.Clamp(a.Zip(b, (x, y) => (double)x * y).Sum(), -1, 1);

    public static List<float[]> SelectRepresentatives(IEnumerable<float[]> candidates)
    {
        var distinct = new List<float[]>();
        foreach (var candidate in candidates)
            if (!distinct.Any(e => Cosine(e, candidate) > 0.98)) distinct.Add(candidate.ToArray());
        if (distinct.Count <= SpeakerRegistry.MaximumRepresentatives) return distinct;
        var selected = new List<float[]> { distinct[0] };
        while (selected.Count < SpeakerRegistry.MaximumRepresentatives)
            selected.Add(distinct.Where(e => !selected.Contains(e))
                .MinBy(e => selected.Max(s => Cosine(s, e)))!);
        return selected;
    }
}
