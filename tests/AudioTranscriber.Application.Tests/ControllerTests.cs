using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AudioTranscriber.Application;
using AudioTranscriber.Core;
using AudioTranscriber.Diarization;
using AudioTranscriber.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AudioTranscriber.Application.Tests;

public sealed class ControllerTests
{
    [Fact]
    public async Task StopSealsTailWithoutCancelingSlowRecognition()
    {
        await using var fixture = new Fixture();
        var session = await fixture.App.StartRecordingAsync("Synthetic capture", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        Assert.False(fixture.App.IsRecording);
        Assert.Equal(38000, fixture.App.Store.GetChunks(fixture.Capture.TrackId).Sum(chunk => chunk.SampleCount));
        Assert.Equal("Recorded", fixture.App.Store.GetSession(session.Id).State);
        await UntilAsync(() => fixture.Provider.Calls > 0);
        Assert.False(fixture.Provider.WasCanceled);
        fixture.Provider.Release.TrySetResult();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 4);
        var transcript = fixture.App.Store.GetTranscriptPage(session.Id);
        Assert.Equal(2, transcript.Count);
        Assert.All(transcript, row => Assert.Equal("Speaker 1", row.SpeakerName));
        Assert.Equal(21_000_000, transcript[1].StartTicks);
    }

    [Fact]
    public async Task CloudConsentGatesEveryQueuedSessionAndNeverImplicitlyOptsIn()
    {
        await using var fixture = new Fixture("nvidia-parakeet-tdt-v3");
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Public synthetic consent test", "synthetic-output", null,
            "nvidia-parakeet-tdt-v3", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 2);
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.False(fixture.App.Store.GetSession(session.Id).CloudConsent);
        fixture.App.SetCloudConsent(session.Id, true);
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 4);
        Assert.Equal(2, fixture.Provider.Calls);
    }

    [Fact]
    public async Task PauseRejectsLateResultsAndResumeReusesDurableChunks()
    {
        await using var fixture = new Fixture();
        var session = await fixture.App.StartRecordingAsync("Synthetic pause", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.Provider.Calls > 0);
        fixture.App.PauseTranscription(session.Id);
        await UntilAsync(() => fixture.Provider.WasCanceled);
        Assert.Empty(fixture.App.Store.GetTranscriptPage(session.Id));
        fixture.Provider.Release.TrySetResult();
        fixture.App.ResumeTranscription(session.Id);
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 4);
        Assert.Equal(2, fixture.App.Store.GetTranscriptPage(session.Id).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspensionIncludesChunksSealedAfterPauseOrCancel(bool canceled)
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Synthetic future chunks", "synthetic-output", null,
            "local-whisper", "en", false);
        if (canceled) fixture.App.CancelTranscription(session.Id);
        else fixture.App.PauseTranscription(session.Id);
        fixture.Capture.Emit(32000);
        fixture.Capture.Emit(16000);
        await fixture.App.StopRecordingAsync();
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.All(fixture.App.Store.GetJobs(session.Id), job => Assert.Equal(canceled ? "Canceled" : "Paused", job.State));
        Assert.Equal(canceled ? "Canceled" : "Paused", fixture.App.Store.GetSession(session.Id).ProcessingState);
        fixture.App.ResumeTranscription(session.Id);
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 6);
        Assert.Equal(3, fixture.Provider.Calls);
    }

    [Fact]
    public async Task InvalidRetainedAudioFailsOnlyAffectedJobs()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        fixture.Media.CorruptOutput = true;
        var bad = await fixture.App.StartRecordingAsync("Synthetic invalid bytes", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(bad.Id).Failed == 2);
        fixture.Media.CorruptOutput = false;
        var good = await fixture.App.StartRecordingAsync("Synthetic valid bytes", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(good.Id).Succeeded == 4);
    }

    [Fact]
    public async Task FailedInitialMediaCheckpointDoesNotLeaveAStaleRegistration()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var input = Path.Combine(fixture.Root, "synthetic.pcm");
        await File.WriteAllBytesAsync(input, new byte[64000]);
        fixture.Sql("""
            CREATE TRIGGER reject_media_start BEFORE UPDATE OF state ON sessions WHEN new.state='Importing'
            BEGIN SELECT RAISE(ABORT,'Synthetic one-time checkpoint failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => fixture.App.ImportAudioAsync(
            "Synthetic checkpoint recovery", input, 0, "local-whisper", "en", false));
        var session = Assert.Single(fixture.App.Store.GetSessions());
        fixture.Sql("DROP TRIGGER reject_media_start");
        fixture.App.ResumeTranscription(session.Id);
        await UntilAsync(() => fixture.App.Store.GetSession(session.Id).State == "Recorded");
        Assert.Equal(1, fixture.Media.CopyCalls);
    }

    [Fact]
    public async Task ResumeRecoversSealedImportWithoutTheExternalSource()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var session = fixture.App.Store.CreateSession("Synthetic orphan recovery", "local-whisper", "en");
        var trackId = Guid.NewGuid();
        var directory = Path.Combine(session.Directory, "imports");
        Directory.CreateDirectory(directory);
        var retained = Path.Combine(directory, "retained.pcm");
        var bytes = new byte[64000];
        await File.WriteAllBytesAsync(retained, bytes);
        var source = Path.Combine(fixture.Root, "no-longer-present.pcm");
        var manifest = new AudioTranscriber.Audio.ManagedImport(session.Id, trackId, retained, source, "source.pcm",
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            new(0, "pcm_s16le", 16000, 1, "s16", "mono", TimeSpan.FromSeconds(2).Ticks, 0, "1/16000"),
            DateTimeOffset.UtcNow, "synthetic-test", "synthetic-probe");
        await File.WriteAllTextAsync(retained + ".import.json", JsonSerializer.Serialize(manifest));
        fixture.App.Store.AddTrack(new(trackId, session.Id, "Imported", "Synthetic import", null, 0,
            JsonSerializer.Serialize(new { SourcePath = source, StreamIndex = 0, Imported = (object?)null })));
        fixture.App.Store.SetSessionState(session.Id, "Recoverable");
        fixture.App.ResumeTranscription(session.Id);
        await UntilAsync(() => fixture.App.Store.GetSession(session.Id).State == "Recorded");
        Assert.Equal(0, fixture.Media.CopyCalls);
        Assert.Equal(retained, Assert.Single(fixture.App.Store.GetTracks(session.Id)).OriginalPath);
    }

    [Fact]
    public async Task TransientLeaseWriteFailureDoesNotTerminateTheScheduler()
    {
        await using var fixture = new Fixture();
        var session = await fixture.App.StartRecordingAsync("Synthetic lease recovery", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.Provider.Calls > 0);
        fixture.Sql("""
            CREATE TRIGGER reject_renewal BEFORE UPDATE OF lease_until ON jobs
            WHEN old.state='Running' AND new.state='Running'
            BEGIN SELECT RAISE(ABORT,'Synthetic transient lease failure'); END;
            """);
        await UntilAsync(() => fixture.Notifications.Any(item => item.Message.StartsWith("Lease renewal is temporarily", StringComparison.Ordinal)), 45);
        fixture.Sql("DROP TRIGGER reject_renewal");
        fixture.Provider.Release.TrySetResult();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 4);
    }

    private static async Task UntilAsync(Func<bool> condition, int seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AudioTranscriber-controller-" + Guid.NewGuid().ToString("N"));
        public FakeCapture Capture { get; } = new();
        public FakeMedia Media { get; } = new();
        public FakeProvider Provider { get; }
        public AppController App { get; }
        public ConcurrentQueue<AppNotification> Notifications { get; } = new();
        public Fixture(string provider = "local-whisper")
        {
            Provider = new(provider);
            App = new(Root, Capture, Media, new FakePlayback(), _ => Provider, () => new FakeDiarizer());
            App.Notification += Notifications.Enqueue;
        }
        public void Sql(string commandText)
        {
            using var connection = new SqliteConnection("Data Source=" + Path.Combine(Root, "library.sqlite3"));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.ExecuteNonQuery();
        }
        public async ValueTask DisposeAsync()
        {
            Provider.Release.TrySetResult();
            await App.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FakeCapture : IAudioCaptureService
    {
        private CaptureOptions? options;
        private readonly Guid continuity = Guid.NewGuid();
        private long frames;
        private bool stopped;
        public Guid TrackId => options?.OutputTrackId ?? Guid.Empty;
        public event Action<NativeChunk>? ChunkSealed;
        public event Action<AudioLevels>? Levels;
        public event Action<CaptureFault>? Fault;
        public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() =>
            [new("synthetic-output", "Synthetic output, no desktop capture", TrackKind.Loopback, true, true)];
        public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() => [];
        public Task<CaptureSession> StartAsync(CaptureOptions request, CancellationToken cancellationToken = default)
        {
            options = request;
            frames = 0;
            stopped = false;
            Directory.CreateDirectory(request.OutputDirectory);
            return Task.FromResult(new CaptureSession(request.SessionId, 0,
                [new(TrackId, request.SessionId, TrackKind.Loopback, "Synthetic", AudioFormat.Pcm16Mono16K)]));
        }
        public void Emit(int samples)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(options!.OutputDirectory, id.ToString("N") + ".synthetic-pcm");
            File.WriteAllBytes(path, new byte[samples * 2]);
            ChunkSealed?.Invoke(new(id, TrackId, path, AudioFormat.Pcm16Mono16K, frames, samples,
                AudioTime.FramesToTicks(frames, 16000), continuity));
            frames += samples;
            Levels?.Invoke(new(TrackId, 0, 0, 0));
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!stopped && options is not null) { stopped = true; Emit(6000); }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { GC.KeepAlive(Fault); return ValueTask.CompletedTask; }
    }

    private sealed class FakeMedia : IMediaNormalizer
    {
        public bool CorruptOutput { get; set; }
        public int CopyCalls { get; private set; }
        public async IAsyncEnumerable<NormalizedChunk> NormalizeAsync(IAsyncEnumerable<NativeChunk> chunks,
            NormalizationOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(options.OutputDirectory);
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                var path = Path.Combine(options.OutputDirectory, chunk.Id.ToString("N") + ".pcm16");
                var bytes = await File.ReadAllBytesAsync(chunk.Path, cancellationToken);
                await File.WriteAllBytesAsync(path, CorruptOutput ? bytes[..^2] : bytes, cancellationToken);
                yield return new(chunk.Id, chunk.TrackId, path, chunk.SourceFrameOffset, chunk.SourceFrameCount,
                    chunk.SourceFrameOffset, chunk.SourceFrameCount, chunk.Format, chunk.SessionStartTicks, chunk.ContinuityId);
            }
        }
        public Task<MediaProbe> ProbeAsync(string localPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaProbe(localPath, new FileInfo(localPath).Length, new FileInfo(localPath).Length / 32000.0,
                [new(0, "synthetic-pcm", AudioFormat.Pcm16Mono16K, 0, null, "1/16000", "en", null)], "synthetic"));
        public async Task<ImportedMedia> CopyImportAsync(MediaImportRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CopyCalls++;
            Directory.CreateDirectory(request.OutputDirectory);
            var path = Path.Combine(request.OutputDirectory, request.TrackId.ToString("N") + ".synthetic-pcm");
            var bytes = await File.ReadAllBytesAsync(request.SourcePath, cancellationToken);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return new(request.TrackId, path, Path.GetFileName(request.SourcePath), Convert.ToHexString(SHA256.HashData(bytes)),
                0, await ProbeAsync(path, cancellationToken), DateTimeOffset.UtcNow);
        }
        public async IAsyncEnumerable<NormalizedChunk> NormalizeImportAsync(ImportedMedia media, NormalizationOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = new FileInfo(media.ManagedOriginalPath).Length / 2;
            yield return new(Guid.NewGuid(), media.TrackId, media.ManagedOriginalPath, 0, samples, 0, samples,
                AudioFormat.Pcm16Mono16K, 0);
            await Task.CompletedTask;
        }
    }

    private sealed class FakeProvider(string id) : ITranscriptionProvider
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool WasCanceled { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderDescriptor Descriptor { get; } = new(id, "Synthetic test provider", "no-model",
            id.StartsWith("nvidia-", StringComparison.Ordinal), TimingGranularity.Word);
        public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            request.Validate();
            Interlocked.Increment(ref calls);
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { WasCanceled = true; throw; }
            var start = request.CoreStartSample * 1000 / 16000 + 100;
            return new(id, "synthetic", TranscriptionStatus.Succeeded,
                [new("hello", start, start + 100, TimingGranularity.Word, [new("hello", start, start + 100)])],
                "{\"synthetic\":true}");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDiarizer : IDiarizationService
    {
        public Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, SpeakerRegistrySnapshot registry,
            CancellationToken cancellationToken = default)
        {
            var end = request.SessionStartTicks + AudioTime.FramesToTicks(request.SampleCount, 16000);
            var speaker = registry.Speakers.FirstOrDefault();
            if (speaker is null)
            {
                var identity = new SpeakerIdentity(Guid.NewGuid(), request.SessionId, 1, "Speaker 1");
                var vector = Enumerable.Range(0, 256).Select(index => index == 0 ? 1f : 0f).ToImmutableArray();
                var embedding = new SpeakerEmbedding(Guid.NewGuid(), request.SessionId, identity.Id,
                    DiarizationModels.EmbeddingSha256, vector, request.SessionStartTicks, end);
                speaker = new(identity, DiarizationModels.EmbeddingSha256, vector, [embedding], end - request.SessionStartTicks);
            }
            return Task.FromResult(new DiarizationResult(
                [new(request.TrackId, request.SessionStartTicks, end, speaker.Identity.Id)],
                new(request.SessionId, registry.Revision + 1, [speaker])));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlayback : IAudioPlaybackService
    {
        public event Action<PlaybackPosition>? PositionChanged;
        public Task PlayAsync(PlaybackRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { GC.KeepAlive(PositionChanged); return ValueTask.CompletedTask; }
    }
}
