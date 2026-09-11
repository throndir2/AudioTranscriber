using System.Collections.Immutable;

namespace AudioTranscriber.Core;

public enum JobKind { Normalize, Transcribe, Diarize, Export }
public enum JobState { Pending, Running, Succeeded, RetryWaiting, Paused, Blocked, Failed, Canceled }
public enum ConsentState { NotGranted, Granted, Revoked }

public sealed record JobRecord(
    Guid Id, Guid SessionId, Guid TrackId, Guid ChunkId, JobKind Kind, JobState State,
    string ProviderId, string ConfigurationIdentity, string AlgorithmVersion,
    long SourceFrameOffset, long SourceFrameCount, int Attempts = 0,
    DateTimeOffset? NotBeforeUtc = null, string? LeaseToken = null,
    DateTimeOffset? LeaseExpiresUtc = null, ProviderError? Error = null);

public sealed record CloudConsent(
    Guid SessionId, string ProviderId, ImmutableArray<Guid> TrackIds, ConsentState State,
    DateTimeOffset UpdatedUtc, string DisclosureVersion)
{
    public bool Allows(Guid sessionId, string providerId, Guid trackId) =>
        State == ConsentState.Granted && SessionId == sessionId &&
        string.Equals(ProviderId, providerId, StringComparison.Ordinal) &&
        !TrackIds.IsDefault && TrackIds.Contains(trackId);
}

public interface ICloudConsentStore
{
    Task<CloudConsent?> GetAsync(Guid sessionId, string providerId, CancellationToken cancellationToken = default);
}

public sealed record TranscriptCorrection(
    Guid SegmentId, long Revision, string Text, DateTimeOffset UpdatedUtc);
