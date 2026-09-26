using System.Collections.Immutable;

namespace AudioTranscriber.Core;

public enum AudioEncoding { PcmInteger, IeeeFloat }
public enum TrackKind { Loopback, Microphone, Imported }
public enum SessionState { Created, Starting, Recording, Stopping, Recorded, Faulted, Recoverable }

[Flags]
public enum AudioChunkFlags { None = 0, Silence = 1, Discontinuity = 2, TimestampError = 4, Recovered = 8 }

public sealed record AudioFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    AudioEncoding Encoding,
    uint ChannelMask = 0,
    int? ValidBitsPerSample = null)
{
    public static AudioFormat Pcm16Mono16K { get; } = new(16_000, 1, 16, AudioEncoding.PcmInteger);
    public int BlockAlign => checked(Channels * (BitsPerSample / 8));
    public long BytesPerSecond => checked((long)SampleRate * BlockAlign);

    public void Validate()
    {
        if (SampleRate <= 0 || Channels is < 1 or > 64 ||
            BitsPerSample is not (8 or 16 or 24 or 32 or 64) ||
            !Enum.IsDefined(Encoding) ||
            (Encoding == AudioEncoding.IeeeFloat && BitsPerSample is not (32 or 64)) ||
            (ValidBitsPerSample.HasValue && (ValidBitsPerSample <= 0 || ValidBitsPerSample > BitsPerSample)))
            throw new ArgumentException("Invalid native audio format.");
    }
}

public sealed record SessionRecord(
    Guid Id, string Name, string Directory, DateTimeOffset CreatedUtc, SessionState State,
    long DurationTicks = 0, string? Error = null);

public sealed record TrackRecord(
    Guid Id, Guid SessionId, TrackKind Kind, string Name, AudioFormat NativeFormat,
    string? DeviceId = null, string? OriginalPath = null, int? AudioStreamIndex = null);

// Source frame offsets count interleaved native frames, not individual channel samples.
// Session ticks are 100 ns relative to the session origin, never DateTime ticks.
public sealed record NativeChunk(
    Guid Id, Guid TrackId, string Path, AudioFormat Format,
    long SourceFrameOffset, long SourceFrameCount, long SessionStartTicks,
    Guid ContinuityId = default, AudioChunkFlags Flags = AudioChunkFlags.None)
{
    public long SourceEndFrame => checked(SourceFrameOffset + SourceFrameCount);
    public long SessionEndTicks => checked(SessionStartTicks + AudioTime.FramesToTicks(SourceFrameCount, Format.SampleRate));
}

// The file is headerless, little-endian signed PCM16 at 16 kHz, mono.
// NormalizedStartSample is track-global; SourceFrameOffset is native-rate.
public sealed record NormalizedChunk(
    Guid Id, Guid TrackId, string Path, long NormalizedStartSample, long SampleCount,
    long SourceFrameOffset, long SourceFrameCount, AudioFormat SourceFormat, long SessionStartTicks,
    Guid ContinuityId = default, Guid? NativeChunkId = null)
{
    public const int SampleRate = 16_000;
    public long NormalizedEndSample => checked(NormalizedStartSample + SampleCount);
    public long SessionEndTicks => checked(SessionStartTicks + AudioTime.FramesToTicks(SampleCount, SampleRate));
}

public sealed record AudioClockAnchor(
    Guid TrackId, Guid ContinuityId, long SourceFrameOffset, long Qpc100Nanoseconds, long SessionTicks);

public enum AudioGapKind { KnownSilence, Unavailable, Discontinuity, Unarchived }
public sealed record AudioGap(
    Guid TrackId, long SessionStartTicks, long DurationTicks, AudioGapKind Kind,
    long? SourceFrameOffset = null, long? SourceFrameCount = null, string? Diagnostic = null);

public sealed record AudioDeviceInfo(
    string Id, string Name, TrackKind Kind, bool IsDefault, bool IsAvailable,
    AudioFormat? MixFormat = null);

public sealed record CaptureOptions(
    Guid SessionId, string OutputDirectory, string OutputDeviceId, string? MicrophoneDeviceId = null,
    Guid? OutputTrackId = null, Guid? MicrophoneTrackId = null,
    int ChunkDurationSeconds = 30, long MaximumChunkBytes = 64L * 1024 * 1024,
    long QueueCapacityBytes = 128L * 1024 * 1024, long MinimumFreeBytes = 512L * 1024 * 1024,
    int PauseSplitAfterMilliseconds = 0);

public sealed record CaptureSession(
    Guid SessionId, long QpcOrigin100Nanoseconds, ImmutableArray<TrackRecord> Tracks);

public sealed record AudioLevels(Guid TrackId, long SessionTicks, float Peak, float Rms);
public sealed record CaptureFault(
    string Code, string Message, Guid? TrackId = null, long? SessionStartTicks = null,
    long? UnarchivedFrameCount = null, bool OriginalAudioMayBeIncomplete = true);

public interface IAudioCaptureService : IAsyncDisposable
{
    event Action<NativeChunk>? ChunkSealed;
    event Action<AudioLevels>? Levels;
    event Action<CaptureFault>? Fault;
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices();
    Task<CaptureSession> StartAsync(CaptureOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
