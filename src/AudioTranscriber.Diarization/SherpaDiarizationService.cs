using System.Buffers.Binary;
using System.Collections.Immutable;
using AudioTranscriber.Core;
using SherpaOnnx;

namespace AudioTranscriber.Diarization;

/// <summary>In-process engine for the isolated worker and tests, not UI-process inference.</summary>
public sealed class SherpaDiarizationService(DiarizationModelPaths models, SpeakerMatchingOptions? matchingOptions = null)
    : IDiarizationService, ISpeakerEnrollmentService, ISpeakerEmbeddingService
{
    public const int MaximumSeconds = 60;
    public const int MaximumEmbeddingClips = 1000;
    public const int MaximumEmbeddingClipSeconds = 30;
    public const string AlgorithmVersion = "sherpa-1.13.8-clean-registry-v1";
    private readonly SpeakerMatchingOptions options = matchingOptions ?? new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private OfflineSpeakerDiarization? diarizer;
    private SpeakerEmbeddingExtractor? extractor;
    private bool disposed;

    public async Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, SpeakerRegistrySnapshot registry,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, registry);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            options.Validate();
            var state = CoreRegistrySerializer.ToState(registry);
            var (samples, turns) = await SegmentAsync(request, cancellationToken);
            var diagnostics = new List<string> { AlgorithmVersion };
            var evidence = ExtractEvidence(samples, turns, diagnostics, cancellationToken);
            var matches = new SpeakerReconciler(options).Reconcile(state, turns, evidence, cancellationToken);
            var snapshot = CoreRegistrySerializer.ToSnapshot(state, registry, request, evidence, matches);
            var coreStart = request.CoreStartSample / 16000.0;
            var coreEnd = (request.CoreStartSample + (request.CoreSampleCount ?? request.SampleCount - request.CoreStartSample)) / 16000.0;
            var output = ImmutableArray.CreateBuilder<SpeakerTurn>();
            foreach (var turn in turns)
            {
                var match = matches.Single(m => m.LocalSpeaker == turn.LocalSpeaker);
                var number = match.SpeakerId is null ? (int?)null : int.Parse(match.SpeakerId.AsSpan(7));
                var stable = number is null ? null : snapshot.Speakers.Single(s => s.Identity.Number == number).Identity;
                foreach (var (start, end, overlap) in CleanSpeech.SplitAtOverlap(turn, turns))
                {
                    var from = Math.Max(coreStart, start);
                    var to = Math.Min(coreEnd, end);
                    if (to <= from) continue;
                    var startSample = (long)Math.Round(from * 16000);
                    var endSample = (long)Math.Round(to * 16000);
                    if (endSample <= startSample) continue;
                    var quality = SpeakerQualityFlags.None;
                    if (overlap) quality |= SpeakerQualityFlags.Overlap | SpeakerQualityFlags.Ambiguous;
                    if (match.Quality == "InsufficientCleanSpeech") quality |= SpeakerQualityFlags.InsufficientEvidence;
                    else if (stable is null) quality |= SpeakerQualityFlags.Ambiguous;
                    if (end - start < 1.5) quality |= SpeakerQualityFlags.ShortTurn;
                    output.Add(new(request.TrackId,
                        checked(request.SessionStartTicks + AudioTime.FramesToTicks(startSample, 16000)),
                        checked(request.SessionStartTicks + AudioTime.FramesToTicks(endSample, 16000)),
                        stable?.Id, $"{AlgorithmVersion}:{turn.LocalSpeaker}", quality, match.Similarity,
                        checked(request.NormalizedStartSample + startSample), checked(request.NormalizedStartSample + endSample),
                        request.SourceSampleRate is { } startRate
                            ? checked(request.SourceFrameOffset + AudioTime.Scale(startSample, startRate, 16000)) : null,
                        request.SourceSampleRate is { } endRate
                            ? checked(request.SourceFrameOffset + AudioTime.Scale(endSample, endRate, 16000)) : null));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (samples.Length < 30 * 16000) diagnostics.Add("ShortInputSilencePaddedTo30Seconds;OutputClippedToOwnedCore");
            if (output.Count == 0) diagnostics.Add("NoSpeechDetected");
            return new(output.OrderBy(t => t.StartTicks).ThenBy(t => t.EndTicks).ToImmutableArray(), snapshot, diagnostics.ToImmutableArray());
        }
        finally { gate.Release(); }
    }

    /// <summary>Adds the dominant clean voice in the request audio to the named identity (created if missing).</summary>
    public async Task<DiarizationResult> EnrollAsync(DiarizationRequest request, SpeakerRegistrySnapshot registry,
        SpeakerEnrollment enrollment, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, registry);
        ValidateEnrollment(enrollment);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            options.Validate();
            var state = CoreRegistrySerializer.ToState(registry);
            var (samples, turns) = await SegmentAsync(request, cancellationToken);
            var diagnostics = new List<string> { AlgorithmVersion, "UserEnrollment" };
            var best = ExtractEvidence(samples, turns, diagnostics, cancellationToken)
                .OrderByDescending(e => e.CleanSpeechSeconds).FirstOrDefault();
            if (best is null)
            {
                diagnostics.Add("EnrollmentSkipped:InsufficientCleanSpeech");
                return new([], registry, diagnostics.ToImmutableArray());
            }
            var vector = best.Embedding;
            var target = registry.Speakers.FirstOrDefault(e => e.Identity.Id == enrollment.SpeakerId);
            var targetProfile = target is null ? null : state.ResolveAlias($"Speaker{target.Identity.Number}");
            // Report another profile that already confidently owns this voice, so the caller can offer a merge.
            var mergedTargets = state.Aliases.Keys.Select(state.ResolveAlias).ToHashSet();
            var ranked = state.Speakers.Where(p => p.SpeakerId != targetProfile).Select(p => (p.SpeakerId, Score:
                    mergedTargets.Contains(p.SpeakerId)
                        ? p.Representatives.Max(r => Embeddings.Cosine(r, vector))
                        : (Embeddings.Cosine(p.Centroid, vector) + p.Representatives.Max(r => Embeddings.Cosine(r, vector))) / 2))
                .OrderByDescending(p => p.Score).ToArray();
            if (ranked.Length > 0 && ranked[0].Score >= options.MatchThreshold &&
                ranked[0].Score - (ranked.Length > 1 ? ranked[1].Score : -1) >= options.RunnerUpMargin)
            {
                var similarNumber = int.Parse(ranked[0].SpeakerId.AsSpan(7));
                var similar = registry.Speakers.First(e => e.Identity.Number == similarNumber).Identity;
                diagnostics.Add($"SimilarTo:{similar.Id:D}:{ranked[0].Score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            SpeakerIdentity? created = null;
            string profileId;
            if (target is not null)
            {
                profileId = targetProfile!;
                var index = state.Speakers.FindIndex(p => p.SpeakerId == profileId);
                var profile = state.Speakers[index];
                var weight = Math.Min(profile.ObservationCount, 20);
                state.Speakers[index] = profile with
                {
                    Centroid = Embeddings.Normalize(profile.Centroid.Zip(vector, (a, b) => a * weight + b).ToArray()),
                    Representatives = Embeddings.SelectRepresentatives(profile.Representatives.Append(vector)),
                    ObservationCount = Math.Min(1_000_000, profile.ObservationCount + 1)
                };
            }
            else
            {
                if (state.Speakers.Count + state.Aliases.Count >= SpeakerRegistry.MaximumSpeakers || state.NextSpeakerNumber >= 1_000_000)
                {
                    diagnostics.Add("EnrollmentSkipped:RegistryFull");
                    return new([], registry, diagnostics.ToImmutableArray());
                }
                var number = state.NextSpeakerNumber++;
                profileId = $"Speaker{number}";
                state.Speakers.Add(new(profileId, vector, [vector.ToArray()], 1));
                created = new SpeakerIdentity(enrollment.SpeakerId, request.SessionId, number, enrollment.DisplayName.Trim(),
                    Provenance: "User-labeled voice sample");
            }
            state.Revision++;
            var snapshot = CoreRegistrySerializer.ToSnapshot(state, registry, request, [best],
                [new SpeakerMatch(best.LocalSpeaker, profileId, null, "UserEnrolled")],
                number => created is not null && created.Number == number ? created : null);
            diagnostics.Add($"Enrolled:{best.CleanSpeechSeconds:0.0}s");
            return new([], snapshot, diagnostics.ToImmutableArray());
        }
        finally { gate.Release(); }
    }

    public static void ValidateEnrollment(SpeakerEnrollment enrollment)
    {
        if (enrollment is null || enrollment.SpeakerId == Guid.Empty || string.IsNullOrWhiteSpace(enrollment.DisplayName) ||
            enrollment.DisplayName.Length > 1024)
            throw new ArgumentException("Speaker enrollment requires a speaker ID and display name.");
    }

    /// <summary>One voice fingerprint per clip, straight from the embedding model (no segmentation or clustering).</summary>
    public async Task<IReadOnlyList<float[]?>> EmbedAsync(string audioPath, long sampleCount, IReadOnlyList<SpeakerEmbeddingClip> clips,
        CancellationToken cancellationToken = default)
    {
        ValidateEmbedding(audioPath, sampleCount, clips);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (extractor is null)
            {
                await DiarizationModels.VerifyAsync(models, cancellationToken);
                var config = new SpeakerEmbeddingExtractorConfig
                {
                    Model = Path.GetFullPath(models.EmbeddingModelPath),
                    NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
                };
                extractor = new SpeakerEmbeddingExtractor(config);
                if (extractor.Dim != Embeddings.Dimension) throw new InvalidDataException("Native embedding model format is incompatible.");
            }
            await using var file = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length != sampleCount * 2) throw new InvalidDataException("Embedding audio changed before reading.");
            var result = new float[]?[clips.Count];
            for (var index = 0; index < clips.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clip = clips[index];
                if (clip.SampleCount < 16000) continue;
                var bytes = new byte[clip.SampleCount * 2];
                file.Position = clip.StartSample * 2;
                await file.ReadExactlyAsync(bytes, cancellationToken);
                var samples = new float[clip.SampleCount];
                for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)) / 32768f;
                using var stream = extractor.CreateStream();
                stream.AcceptWaveform(16000, samples);
                stream.InputFinished();
                if (!extractor.IsReady(stream)) continue;
                try { result[index] = Embeddings.Normalize(extractor.Compute(stream)); }
                catch (InvalidDataException) { }
            }
            return result;
        }
        finally { gate.Release(); }
    }

    public static void ValidateEmbedding(string audioPath, long sampleCount, IReadOnlyList<SpeakerEmbeddingClip> clips)
    {
        if (clips is null || clips.Count is 0 or > MaximumEmbeddingClips || sampleCount <= 0 ||
            clips.Any(clip => clip is null || clip.StartSample < 0 || clip.SampleCount is <= 0 or > MaximumEmbeddingClipSeconds * 16000 ||
                              clip.StartSample + clip.SampleCount > sampleCount))
            throw new ArgumentException("Voice fingerprints require bounded clips inside local mono16k PCM16 audio.");
        LocalPaths.RequireFile(audioPath);
        if (new FileInfo(audioPath).Length != sampleCount * 2)
            throw new InvalidDataException("Embedding PCM16 size does not match the declared sample count.");
    }

    public static void ValidateEmbeddings(IReadOnlyList<float[]?> embeddings, int count)
    {
        if (embeddings is null || embeddings.Count != count) throw new InvalidDataException("Worker returned the wrong number of voice fingerprints.");
        foreach (var vector in embeddings) if (vector is not null) Embeddings.Validate(vector);
    }

    private async Task<(float[] Samples, LocalSpeakerTurn[] Turns)> SegmentAsync(DiarizationRequest request, CancellationToken cancellationToken)
    {
        if (diarizer is null)
        {
            await DiarizationModels.VerifyAsync(models, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = Path.GetFullPath(models.SegmentationModelPath);
            config.Segmentation.NumThreads = 2;
            config.Embedding.Model = Path.GetFullPath(models.EmbeddingModelPath);
            config.Embedding.NumThreads = 2;
            config.Clustering.NumClusters = -1;
            config.Clustering.Threshold = 0.5f;
            diarizer = new OfflineSpeakerDiarization(config);
            extractor ??= new SpeakerEmbeddingExtractor(config.Embedding);
            if (diarizer.SampleRate != 16000 || extractor.Dim != Embeddings.Dimension)
                throw new InvalidDataException("Native diarization model format is incompatible.");
        }
        var samples = await ReadSamplesAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Sherpa skips clustering at <=10 seconds. Silence-pad short tails to a bounded 30-second call.
        var input = samples.Length < 30 * 16000 ? new float[30 * 16000] : samples;
        if (!ReferenceEquals(input, samples)) samples.CopyTo(input, 0);
        var nativeTurns = diarizer.Process(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (nativeTurns.Length > 2048) throw new InvalidDataException("Native diarization returned too many turns.");
        var duration = request.SampleCount / 16000.0;
        if (nativeTurns.Any(t => !float.IsFinite(t.Start) || !float.IsFinite(t.End) || t.Speaker < 0))
            throw new InvalidDataException("Native diarization returned invalid intervals.");
        var turns = nativeTurns.Select(t => new LocalSpeakerTurn(Math.Max(0, t.Start), Math.Min(duration, t.End), t.Speaker))
            .Where(t => t.EndSeconds > t.StartSeconds).Distinct().OrderBy(t => t.StartSeconds).ToArray();
        return (samples, turns);
    }

    private List<SpeakerEvidence> ExtractEvidence(float[] samples, LocalSpeakerTurn[] turns, List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var evidence = new List<SpeakerEvidence>();
        foreach (var local in turns.Select(t => t.LocalSpeaker).Distinct().Order())
        {
            var vectors = new List<float[]>();
            double seconds = 0;
            var evidenceStart = double.PositiveInfinity;
            double evidenceEnd = 0;
            foreach (var clean in CleanSpeech.Intervals(turns, local).Take(3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = (int)Math.Ceiling(clean.StartSeconds * 16000);
                var end = Math.Min(samples.Length, Math.Min((int)Math.Floor(clean.EndSeconds * 16000), start + 8 * 16000));
                if (end - start < 1.5 * 16000) continue;
                var isolated = samples.AsSpan(start, end - start).ToArray();
                using var stream = extractor!.CreateStream();
                stream.AcceptWaveform(16000, isolated);
                stream.InputFinished();
                if (!extractor.IsReady(stream)) continue;
                vectors.Add(Embeddings.Normalize(extractor.Compute(stream)));
                seconds += isolated.Length / 16000.0;
                evidenceStart = Math.Min(evidenceStart, start / 16000.0);
                evidenceEnd = Math.Max(evidenceEnd, end / 16000.0);
            }
            if (seconds < options.MinimumCleanSpeechSeconds || vectors.Count == 0) continue;
            // A locally mixed cluster must not poison the persistent centroid.
            if (vectors.Any(a => vectors.Any(b => Embeddings.Cosine(a, b) < options.NewSpeakerThreshold)))
            {
                diagnostics.Add($"LocalSpeaker{local}:InconsistentCleanEvidence");
                continue;
            }
            var average = new float[Embeddings.Dimension];
            foreach (var vector in vectors)
                for (var i = 0; i < average.Length; i++) average[i] += vector[i];
            evidence.Add(new(local, Embeddings.Normalize(average), seconds, evidenceStart, evidenceEnd));
        }
        return evidence;
    }

    public static void ValidateRequest(DiarizationRequest request, SpeakerRegistrySnapshot registry)
    {
        if (request.SessionId == Guid.Empty || request.TrackId == Guid.Empty || registry.SessionId != request.SessionId ||
            request.SampleCount is <= 0 or > MaximumSeconds * 16000 || request.SessionStartTicks < 0 ||
            request.NormalizedStartSample < 0 || request.SourceFrameOffset < 0 || request.SourceSampleRate is <= 0 ||
            request.CoreStartSample < 0 || request.CoreStartSample >= request.SampleCount ||
            (request.CoreSampleCount ?? request.SampleCount - request.CoreStartSample) <= 0 ||
            (request.CoreSampleCount ?? request.SampleCount - request.CoreStartSample) > request.SampleCount - request.CoreStartSample)
            throw new ArgumentException("Diarization requires bounded local mono16k PCM16 audio and a matching session registry.");
        _ = checked(request.SessionStartTicks + AudioTime.FramesToTicks(request.SampleCount, 16000));
        _ = checked(request.NormalizedStartSample + request.SampleCount);
        if (request.SourceSampleRate is { } rate)
            _ = checked(request.SourceFrameOffset + AudioTime.Scale(request.SampleCount, rate, 16000));
        LocalPaths.RequireFile(request.AudioPath);
        if (new FileInfo(request.AudioPath).Length != request.SampleCount * 2)
            throw new InvalidDataException("Diarization PCM16 size does not match the declared sample count.");
    }

    private static async Task<float[]> ReadSamplesAsync(DiarizationRequest request, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(request.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length != request.SampleCount * 2) throw new InvalidDataException("Diarization audio changed before reading.");
        var bytes = new byte[checked((int)request.SampleCount * 2)];
        await file.ReadExactlyAsync(bytes, cancellationToken);
        var samples = new float[checked((int)request.SampleCount)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)) / 32768f;
        return samples;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            extractor?.Dispose();
            diarizer?.Dispose();
        }
        finally { gate.Release(); }
    }
}
