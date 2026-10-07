using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioTranscriber.Audio;

public sealed record PlaybackTrack(Guid TrackId, IReadOnlyList<OriginalChunk>? Chunks = null,
    ManagedImport? Import = null, float Volume = 1, IReadOnlyList<PlaybackClip>? Clips = null);
public sealed record PlaybackClip(string Path, int StreamIndex, long SessionStartTicks,
    int SourceSampleRate, long SourceStartFrame, long? FrameCount);
public sealed record PlaybackRequest(long SessionTicks, IReadOnlyList<PlaybackTrack> Tracks, string? OutputDeviceId = null);

/// <summary>Bounded local playback; seek tears down prior devices, readers, pipes and owned decoders first.</summary>
public sealed class TimelinePlayer(MediaTools? tools = null) : IAsyncDisposable
{
    private readonly MediaTools tools = tools ?? new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<TrackProvider> tracks = [];
    private CancellationTokenSource? lifetime;
    private AudioOutput? output;
    public event Action<Exception>? PlaybackFailed;

    public async Task PlayAsync(PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SessionTicks < 0 || request.Tracks.Count is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(request));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            foreach (var track in request.Tracks)
            {
                if ((track.Import is null ? 0 : 1) + (track.Chunks is null ? 0 : 1) + (track.Clips is null ? 0 : 1) != 1)
                    throw new ArgumentException("Each playback track needs exactly one original source.");
                var provider = new TrackProvider(track, request.SessionTicks, tools, lifetime.Token,
                    exception => PlaybackFailed?.Invoke(exception));
                tracks.Add(provider);
                provider.Start();
            }
            await Task.WhenAll(tracks.Select(t => t.Ready)).WaitAsync(cancellationToken);
            var mixer = new MixingSampleProvider(tracks) { ReadFully = false };
            output = new AudioOutput(request.OutputDeviceId, mixer);
            output.Play();
        }
        catch { await StopCoreAsync(); throw; }
        finally { gate.Release(); }
    }

    public void SetVolume(Guid trackId, float volume)
    {
        if (!float.IsFinite(volume) || volume is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(volume));
        foreach (var track in tracks) if (track.Id == trackId) track.Volume = volume;
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        output?.Stop();
        output?.Dispose();
        output = null;
        lifetime?.Cancel();
        foreach (var track in tracks) await track.DisposeAsync();
        tracks.Clear();
        lifetime?.Dispose(); lifetime = null;
    }

    public async ValueTask DisposeAsync() { await StopAsync(); gate.Dispose(); }

    private sealed class TrackProvider : ISampleProvider, IAsyncDisposable
    {
        private readonly PlaybackTrack track;
        private readonly long seekTicks;
        private readonly MediaTools tools;
        private readonly CancellationToken token;
        private readonly Action<Exception> failed;
        private readonly Channel<byte[]> buffers = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? producer;
        private byte[]? current;
        private int offset;
        public Guid Id => track.TrackId;
        public float Volume;
        public Task Ready => ready.Task;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public TrackProvider(PlaybackTrack track, long seekTicks, MediaTools tools, CancellationToken token, Action<Exception> failed)
        {
            if (!float.IsFinite(track.Volume) || track.Volume is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(track));
            this.track = track; this.seekTicks = seekTicks; this.tools = tools; this.token = token; this.failed = failed;
            Volume = track.Volume;
        }
        public void Start() => producer = Task.Run(ProduceAsync);

        private async Task ProduceAsync()
        {
            try
            {
                if (track.Import is { } import)
                {
                    await DecodeAsync(import.ManagedPath, import.Stream.Index,
                        AudioTime.TicksToFrames(seekTicks, import.Stream.SampleRate), null, import.Stream.SampleRate);
                }
                else if (track.Clips is not null)
                {
                    long timeline = seekTicks;
                    foreach (var clip in track.Clips.OrderBy(c => c.SessionStartTicks))
                    {
                        var end = clip.FrameCount is long frames
                            ? checked(clip.SessionStartTicks + AudioTime.FramesToTicks(frames, clip.SourceSampleRate)) : long.MaxValue;
                        if (end <= seekTicks) continue;
                        if (clip.SessionStartTicks > timeline)
                            await SilenceAsync(AudioTime.TicksToFrames(clip.SessionStartTicks - timeline, 48000));
                        var skip = AudioTime.TicksToFrames(Math.Max(0, seekTicks - clip.SessionStartTicks), clip.SourceSampleRate);
                        await DecodeAsync(clip.Path, clip.StreamIndex, checked(clip.SourceStartFrame + skip),
                            clip.FrameCount is long length ? checked(clip.SourceStartFrame + length) : null, clip.SourceSampleRate);
                        timeline = end;
                    }
                }
                else
                {
                    long timelineFrame = AudioTime.TicksToFrames(seekTicks, 48000);
                    foreach (var chunk in track.Chunks!.OrderBy(c => c.SessionStartTicks))
                    {
                        if (chunk.SessionEndTicks <= seekTicks) continue;
                        var chunkStart = AudioTime.TicksToFrames(chunk.SessionStartTicks, 48000);
                        if (chunkStart > timelineFrame) await SilenceAsync(chunkStart - timelineFrame);
                        var startFrame = PlaybackTimeline.FrameAt(chunk, seekTicks);
                        var stopFrame = chunk.FrameCount;
                        await DecodeAsync(chunk.Path, 0, startFrame, stopFrame, chunk.Format.SampleRate);
                        timelineFrame = AudioTime.TicksToFrames(chunk.SessionEndTicks, 48000);
                    }
                }
                ready.TrySetResult();
                buffers.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                ready.TrySetCanceled(token);
                buffers.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
                buffers.Writer.TryComplete(exception);
                failed(exception);
            }
        }

        private async Task DecodeAsync(string path, int streamIndex, long sourceStartFrame, long? sourceEndFrame, int sourceRate)
        {
            await using var decoder = LocalPlaybackDecoder.Open(path, streamIndex, sourceRate, sourceStartFrame, sourceEndFrame, tools, token);
            decoder.Input.Close();
            while (true)
            {
                var buffer = new byte[32768];
                var filled = 0;
                while (filled < buffer.Length)
                {
                    var count = await decoder.Output.ReadAsync(buffer.AsMemory(filled), token);
                    if (count == 0) break;
                    filled += count;
                }
                if (filled == 0) break;
                if (filled % 8 != 0) throw new InvalidDataException("Decoder emitted an incomplete stereo float frame.");
                if (filled != buffer.Length) Array.Resize(ref buffer, filled);
                await buffers.Writer.WriteAsync(buffer, token);
                ready.TrySetResult();
            }
            await decoder.CompleteAsync(token);
        }

        private async Task SilenceAsync(long frames)
        {
            while (frames > 0)
            {
                var count = (int)Math.Min(frames, 4096);
                await buffers.Writer.WriteAsync(new byte[count * 8], token);
                ready.TrySetResult();
                frames -= count;
            }
        }

        public int Read(float[] buffer, int destinationOffset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (current is null || offset == current.Length)
                {
                    if (!buffers.Reader.TryRead(out current))
                    {
                        if (buffers.Reader.Completion.IsCompleted) return written;
                        // A decoder underrun is surfaced rather than hidden as source silence.
                        failed(new IOException("Playback decoder underrun; the playback clock contains an output gap."));
                        Array.Clear(buffer, destinationOffset + written, count - written);
                        return count;
                    }
                    offset = 0;
                }
                var samples = Math.Min((current.Length - offset) / 4, count - written);
                var volume = Volume;
                for (var i = 0; i < samples; i++)
                    buffer[destinationOffset + written + i] =
                        BinaryPrimitives.ReadSingleLittleEndian(current.AsSpan(offset + i * 4)) * volume;
                offset += samples * 4;
                written += samples;
            }
            return written;
        }

        public async ValueTask DisposeAsync()
        {
            if (producer is not null) await producer;
            while (buffers.Reader.TryRead(out _)) { }
            current = null;
        }
    }
}
