using System.Collections.Immutable;

namespace AudioTranscriber.Core;

public sealed record NormalizationOptions(
    string OutputDirectory, int CoreDurationSeconds = 24, long InitialNormalizedSample = 0);

public sealed record MediaAudioStream(
    int Index, string Codec, AudioFormat? Format, double? StartSeconds,
    double? DurationSeconds, string? TimeBase, string? Language, string? Title);

public sealed record MediaProbe(
    string Path, long FileSize, double? DurationSeconds, ImmutableArray<MediaAudioStream> AudioStreams,
    string? ToolVersion = null);

public sealed record MediaImportRequest(
    Guid SessionId, Guid TrackId, string SourcePath, int AudioStreamIndex, string OutputDirectory,
    int CoreDurationSeconds = 24);

public sealed record ImportedMedia(
    Guid TrackId, string ManagedOriginalPath, string OriginalFileName, string Sha256,
    int AudioStreamIndex, MediaProbe Probe, DateTimeOffset ImportedUtc);

public interface IMediaNormalizer
{
    // One invocation spans a track's contiguous native chunks and drains at EOF.
    // Discontinuity/format changes must open a new mapped continuity run.
    IAsyncEnumerable<NormalizedChunk> NormalizeAsync(
        IAsyncEnumerable<NativeChunk> chunks, NormalizationOptions options,
        CancellationToken cancellationToken = default);
    Task<MediaProbe> ProbeAsync(string localPath, CancellationToken cancellationToken = default);
    Task<ImportedMedia> CopyImportAsync(
        MediaImportRequest request, IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<NormalizedChunk> NormalizeImportAsync(
        ImportedMedia media, NormalizationOptions options, CancellationToken cancellationToken = default);
}

public sealed record PlaybackSource(
    Guid TrackId, string Path, AudioFormat? NativeFormat, long SessionStartTicks,
    long SourceFrameOffset = 0, long? SourceFrameCount = null, int? AudioStreamIndex = null);

public sealed record PlaybackRequest(
    PlaybackSource Source, long SeekSessionTicks, long MaximumDurationTicks,
    int BufferMilliseconds = 500, float Volume = 1);

public sealed record PlaybackPosition(Guid TrackId, long SessionTicks, bool Ended);

public interface IAudioPlaybackService : IAsyncDisposable
{
    event Action<PlaybackPosition>? PositionChanged;
    // Replacing playback must terminate/dispose the previous owned decoder first.
    Task PlayAsync(PlaybackRequest request, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
