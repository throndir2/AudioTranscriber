using System.Collections.Immutable;

namespace AudioTranscriber.Core;

[Flags]
public enum SpeakerQualityFlags
{
    None = 0, InsufficientEvidence = 1, Overlap = 2, Ambiguous = 4, ShortTurn = 8,
    CoarseAlignment = 16, UserAssigned = 32
}

public sealed record SpeakerIdentity(
    Guid Id, Guid SessionId, int Number, string DisplayName, long Revision = 0,
    Guid? MergedIntoId = null, string? ExternalParticipantId = null, string? Provenance = null);

// Optional sample/frame ranges are half-open, track-global, and null when unknown.
// StartTicks/EndTicks remain the session-relative 100 ns presentation interval.
public sealed record SpeakerTurn(
    Guid TrackId, long StartTicks, long EndTicks, Guid? SpeakerId,
    string? LocalSpeakerId = null, SpeakerQualityFlags Quality = SpeakerQualityFlags.None,
    double? Confidence = null,
    long? NormalizedStartSample = null, long? NormalizedEndSample = null,
    long? SourceStartFrame = null, long? SourceEndFrame = null);

public sealed record SpeakerEmbedding(
    Guid Id, Guid SessionId, Guid SpeakerId, string ModelId, ImmutableArray<float> Values,
    long EvidenceStartTicks, long EvidenceEndTicks, SpeakerQualityFlags Quality = SpeakerQualityFlags.None);

public sealed record SpeakerRegistryEntry(
    SpeakerIdentity Identity, string EmbeddingModelId, ImmutableArray<float> Centroid,
    ImmutableArray<SpeakerEmbedding> Representatives, long EvidenceDurationTicks);

public sealed record SpeakerRegistrySnapshot(
    Guid SessionId, long Revision, ImmutableArray<SpeakerRegistryEntry> Speakers);

public interface ISpeakerEmbeddingRegistry
{
    Task<SpeakerRegistrySnapshot> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task SaveAsync(SpeakerRegistrySnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default);
}

public sealed record DiarizationRequest(
    Guid SessionId, Guid TrackId, string AudioPath, long SampleCount, long SessionStartTicks,
    long CoreStartSample = 0, long? CoreSampleCount = null,
    long NormalizedStartSample = 0, long SourceFrameOffset = 0, int? SourceSampleRate = null);

public sealed record DiarizationResult(
    ImmutableArray<SpeakerTurn> Turns, SpeakerRegistrySnapshot Registry,
    ImmutableArray<string> Diagnostics = default);

public interface IDiarizationService : IAsyncDisposable
{
    Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request, SpeakerRegistrySnapshot registry,
        CancellationToken cancellationToken = default);
}

// The user states that the speech in the request audio belongs to SpeakerId. A missing identity is created
// with DisplayName; an existing one gains the audio as voice evidence for future matching.
public sealed record SpeakerEnrollment(Guid SpeakerId, string DisplayName);

public interface ISpeakerEnrollmentService
{
    Task<DiarizationResult> EnrollAsync(
        DiarizationRequest request, SpeakerRegistrySnapshot registry, SpeakerEnrollment enrollment,
        CancellationToken cancellationToken = default);
}
