using System.Collections.Immutable;

namespace AudioTranscriber.Core;

public enum TimingGranularity { None, Chunk, Segment, Word }
public enum TranscriptionStatus { Succeeded, Empty, Partial }

public sealed record ProviderDescriptor(
    string Id, string Name, string Model, bool IsCloud, TimingGranularity Timing,
    int MaximumAudioSeconds = 30, string? FunctionId = null,
    string? DefaultLanguage = null, bool SupportsDiarization = false);

// All result millisecond offsets are RELATIVE TO SUPPLIED AUDIO, not the owned core
// or the session. CoreStartSample/CoreSampleCount describe the non-overlapping
// region owned by this request inside its potentially context-extended input.
public sealed record TranscriptionRequest(
    Guid SessionId, Guid TrackId, Guid ChunkId, string AudioPath,
    long SampleCount, string Language, long SessionStartTicks,
    long CoreStartSample = 0, long? CoreSampleCount = null,
    long NormalizedStartSample = 0, long SourceFrameOffset = 0)
{
    public const int SampleRate = 16_000;
    public long OwnedCoreSampleCount => CoreSampleCount ?? checked(SampleCount - CoreStartSample);
    public long CoreEndSample => checked(CoreStartSample + OwnedCoreSampleCount);

    public void Validate(int maximumAudioSeconds = 30)
    {
        if (maximumAudioSeconds is <= 0 or > 30 ||
            SampleCount <= 0 || SampleCount > checked((long)maximumAudioSeconds * SampleRate) ||
            CoreStartSample < 0 || CoreStartSample >= SampleCount ||
            OwnedCoreSampleCount <= 0 || CoreEndSample > SampleCount ||
            NormalizedStartSample < 0 || SourceFrameOffset < 0 ||
            string.IsNullOrWhiteSpace(AudioPath) || string.IsNullOrWhiteSpace(Language))
            throw new ArgumentException("Invalid bounded raw PCM16 transcription request.");
    }
}

public sealed record TranscriptionWord(
    string Text, long StartMilliseconds, long EndMilliseconds, double? Confidence = null);

public sealed record TranscriptionSegment(
    string Text, long? StartMilliseconds, long? EndMilliseconds, TimingGranularity Timing,
    ImmutableArray<TranscriptionWord> Words = default, double? Confidence = null);

// Provider text/raw response remain immutable evidence. User corrections belong
// in a distinct revision record and must never overwrite these values.
public sealed record TranscriptionResult(
    string ProviderId, string? ObservedModel, TranscriptionStatus Status,
    ImmutableArray<TranscriptionSegment> Segments, string? RawResponse = null,
    ImmutableArray<string> Diagnostics = default);

public enum ProviderErrorCode
{
    InvalidAudio, UnsupportedLanguage, Authentication, PermissionDenied, QuotaExceeded,
    RateLimited, Unavailable, DeadlineExceeded, InvalidResponse, ModelUnavailable,
    ConsentRequired, Canceled, Unknown
}

public sealed record ProviderError(
    ProviderErrorCode Code, string SafeMessage, bool IsTransient = false, TimeSpan? RetryAfter = null);

public sealed class TranscriptionProviderException : Exception
{
    public ProviderError Error { get; }
    public TranscriptionProviderException(ProviderError error) : base(error.SafeMessage) => Error = error;
}

public sealed record OperationProgress(
    string Stage, long Completed, long? Total = null, string? Message = null,
    Guid? SessionId = null, Guid? TrackId = null);

public interface ITranscriptionProvider : IAsyncDisposable
{
    ProviderDescriptor Descriptor { get; }
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request, CancellationToken cancellationToken = default);
}
