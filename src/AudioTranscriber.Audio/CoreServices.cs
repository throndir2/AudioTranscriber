using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Core = AudioTranscriber.Core;

namespace AudioTranscriber.Audio;

public static class AudioContracts
{
    public static Core.AudioFormat ToCore(this NativeWaveFormat format) => new(format.SampleRate, format.Channels,
        format.BitsPerSample, format.IsFloat ? Core.AudioEncoding.IeeeFloat : Core.AudioEncoding.PcmInteger,
        format.ChannelMask, format.ValidBitsPerSample);
    public static Guid ContinuityId(Guid trackId, long run)
    {
        Span<byte> bytes = stackalloc byte[24];
        trackId.TryWriteBytes(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], run);
        return new Guid(SHA256.HashData(bytes).AsSpan(0, 16));
    }
    public static Core.NativeChunk ToCore(this OriginalChunk chunk)
    {
        var flags = Core.AudioChunkFlags.None;
        if (chunk.Recovered) flags |= Core.AudioChunkFlags.Recovered;
        if (chunk.Clocks.Count > 0 && chunk.Clocks.All(c => (c.Flags & PacketFlags.Silent) != 0)) flags |= Core.AudioChunkFlags.Silence;
        if (chunk.Clocks.Any(c => (c.Flags & PacketFlags.Discontinuity) != 0)) flags |= Core.AudioChunkFlags.Discontinuity;
        if (chunk.Clocks.Any(c => (c.Flags & PacketFlags.TimestampError) != 0)) flags |= Core.AudioChunkFlags.TimestampError;
        return new(chunk.Id, chunk.TrackId, chunk.Path, chunk.Format.ToCore(), chunk.SourceStartFrame, chunk.FrameCount,
            chunk.SessionStartTicks, ContinuityId(chunk.TrackId, chunk.ContinuityId), flags);
    }
    public static Core.NormalizedChunk ToCore(this NormalizedShard shard, Core.AudioFormat sourceFormat, Guid? continuityId = null) =>
        new(shard.Id, shard.TrackId, shard.Path, shard.NormalizedStartSample, shard.SampleCount, shard.SourceStartFrame,
            checked(shard.SourceEndFrame - shard.SourceStartFrame), sourceFormat, shard.SessionStartTicks,
            continuityId ?? ContinuityId(shard.TrackId, shard.ContinuityId));

    internal static OriginalChunk LoadOriginal(Core.NativeChunk chunk)
    {
        var path = LocalMedia.RequireFile(chunk.Path);
        if (File.Exists(path + ".json"))
        {
            var original = JsonSerializer.Deserialize<OriginalChunk>(File.ReadAllText(path + ".json"))
                ?? throw new InvalidDataException("Missing original manifest.");
            if (original.TrackId != chunk.TrackId || original.Id != chunk.Id || original.SourceStartFrame != chunk.SourceFrameOffset ||
                original.FrameCount != chunk.SourceFrameCount || original.SessionStartTicks != chunk.SessionStartTicks ||
                original.Format.ToCore() != chunk.Format)
                throw new InvalidDataException("Native chunk differs from its durable manifest.");
            return original;
        }
        using var wave = new NAudio.Wave.WaveFileReader(path);
        var format = NativeWaveFormat.FromWaveFormat(wave.WaveFormat);
        if (format.ToCore() != chunk.Format || wave.Length / format.BlockAlign != chunk.SourceFrameCount)
            throw new InvalidDataException("Native WAV content does not match its declared format and frame count.");
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        file.Position = 12;
        long offset = -1;
        while (file.Position + 8 <= file.Length)
        {
            var name = reader.ReadUInt32();
            var bytes = reader.ReadUInt32();
            if (name == 0x61746164) { offset = file.Position; break; }
            file.Position = checked(file.Position + bytes + (bytes & 1));
        }
        if (offset < 0) throw new InvalidDataException("WAV has no data chunk.");
        file.Position = 0;
        return new(chunk.Id, Guid.Empty, chunk.TrackId, path, format, chunk.SourceFrameOffset, chunk.SourceFrameCount,
            chunk.SessionStartTicks, 0, offset, wave.Length, Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), []);
    }
}

public sealed class WasapiAudioCaptureService : Core.IAudioCaptureService
{
    private CaptureSession? capture;
    public event Action<Core.NativeChunk>? ChunkSealed;
    public event Action<Core.AudioLevels>? Levels;
    public event Action<Core.CaptureFault>? Fault;
    public event Action<OriginalChunk>? OriginalSealed;
    public event Action<AudioGap>? Gap;
    public event Action<AudioFault>? DetailedFault;
    public RecordingState State => capture?.State ?? RecordingState.Recorded;
    public IReadOnlyList<Core.AudioDeviceInfo> GetOutputDevices() => Devices(true);
    public IReadOnlyList<Core.AudioDeviceInfo> GetMicrophoneDevices() => Devices(false);
    private static IReadOnlyList<Core.AudioDeviceInfo> Devices(bool loopback)
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(loopback ? NAudio.CoreAudioApi.DataFlow.Render : NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.Role.Multimedia);
            defaultId = device.ID;
        }
        catch (System.Runtime.InteropServices.COMException) { }
        return CaptureSession.EnumerateDevices(loopback).Select(d => new Core.AudioDeviceInfo(d.Id, d.Name,
            loopback ? Core.TrackKind.Loopback : Core.TrackKind.Microphone, d.Id == defaultId, true)).ToArray();
    }

    public async Task<Core.CaptureSession> StartAsync(Core.CaptureOptions options, CancellationToken cancellationToken = default)
    {
        if (capture is not null)
        {
            if (capture.State is RecordingState.Starting or RecordingState.Recording or RecordingState.Stopping)
                throw new InvalidOperationException("Recording is already active.");
            await capture.DisposeAsync();
        }
        var output = new CaptureTrackOptions(options.OutputTrackId ?? Guid.NewGuid(), options.OutputDeviceId, true);
        var mic = options.MicrophoneDeviceId is null ? null :
            new CaptureTrackOptions(options.MicrophoneTrackId ?? Guid.NewGuid(), options.MicrophoneDeviceId, false);
        capture = new(new(options.OutputDirectory, options.SessionId, output, mic, options.QueueCapacityBytes,
            options.MinimumFreeBytes, options.ChunkDurationSeconds, options.MaximumChunkBytes, options.PauseSplitAfterMilliseconds,
            options.SessionOffsetTicks, options.PauseMilliseconds));
        capture.ChunkSealed += chunk => { OriginalSealed?.Invoke(chunk); ChunkSealed?.Invoke(chunk.ToCore()); };
        capture.LevelChanged += level => Levels?.Invoke(new(level.TrackId,
            CaptureSession.Qpc100ns() - capture.SessionQpcOrigin100ns, level.Peak, level.Rms));
        capture.Gap += gap => Gap?.Invoke(gap);
        capture.Faulted += fault =>
        {
            DetailedFault?.Invoke(fault);
            Fault?.Invoke(new(fault.Code, fault.Message, fault.TrackId, fault.LostStartTicks,
                Math.Max(0, fault.AcceptedThroughFrame - fault.DurableThroughFrame)));
        };
        await capture.StartAsync(cancellationToken);
        return new(options.SessionId, capture.SessionQpcOrigin100ns, capture.Tracks.Select(t =>
            new Core.TrackRecord(t.TrackId, options.SessionId, t.IsLoopback ? Core.TrackKind.Loopback : Core.TrackKind.Microphone,
                t.Name, t.Format.ToCore(), t.DeviceId)).ToImmutableArray());
    }
    public Task StopAsync(CancellationToken cancellationToken = default) => capture?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    public async ValueTask DisposeAsync() { if (capture is not null) await capture.DisposeAsync(); capture = null; }
}

public sealed class FfmpegMediaNormalizer(MediaTools? tools = null) : Core.IMediaNormalizer
{
    private readonly MediaTools tools = tools ?? new();
    private readonly MediaImporter importer = new(tools);

    public async Task<Core.MediaProbe> ProbeAsync(string localPath, CancellationToken cancellationToken = default) =>
        ConvertProbe(await importer.ProbeAsync(localPath, cancellationToken));

    public async Task<Core.ImportedMedia> CopyImportAsync(Core.MediaImportRequest request,
        IProgress<Core.OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var import = await importer.ImportAsync(request.SessionId, request.TrackId, request.SourcePath,
            request.AudioStreamIndex, request.OutputDirectory, progress is null ? null :
                new Progress<ImportProgress>(p => progress.Report(new Core.OperationProgress("Copying original", p.CopiedBytes, p.TotalBytes))),
            cancellationToken);
        var probe = await importer.ProbeAsync(import.ManagedPath, cancellationToken);
        return new(import.TrackId, import.ManagedPath, import.SourceName, import.Sha256, import.Stream.Index,
            ConvertProbe(probe), import.ImportedAt);
    }

    public async IAsyncEnumerable<Core.NormalizedChunk> NormalizeAsync(IAsyncEnumerable<Core.NativeChunk> chunks,
        Core.NormalizationOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<Core.NormalizedChunk>(8);
        var producer = ProduceAsync();
        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken)) yield return chunk;
            await producer;
        }
        finally { lifetime.Cancel(); await producer; }

        async Task ProduceAsync()
        {
            PersistentNormalizer? converter = null;
            Guid? continuity = null;
            long runIndex = -1, offset = options.InitialNormalizedSample;
            try
            {
                await foreach (var chunk in chunks.WithCancellation(lifetime.Token))
                {
                    var original = AudioContracts.LoadOriginal(chunk);
                    if (converter is null || continuity != chunk.ContinuityId)
                    {
                        if (converter is not null)
                        {
                            await converter.CompleteAsync(lifetime.Token);
                            offset = checked(offset + converter.EmittedSamples);
                            await converter.DisposeAsync();
                        }
                        continuity = chunk.ContinuityId;
                        runIndex++;
                        var mappedContinuity = chunk.ContinuityId;
                        converter = new(new(options.OutputDirectory, original.SessionId, chunk.TrackId, runIndex,
                            chunk.SourceFrameOffset, chunk.SessionStartTicks, original.Format, offset, options.CoreDurationSeconds,
                            options.SealAtSourceChunks), tools, lifetime.Token);
                        converter.ShardSealed += shard => channel.Writer.WriteAsync(
                            shard.ToCore(chunk.Format, mappedContinuity), lifetime.Token).AsTask().GetAwaiter().GetResult();
                    }
                    await converter.AppendChunkAsync(original with { ContinuityId = runIndex }, lifetime.Token);
                }
                if (converter is not null) await converter.CompleteAsync(lifetime.Token);
                channel.Writer.TryComplete();
            }
            catch (Exception exception) { channel.Writer.TryComplete(exception); }
            finally { if (converter is not null) await converter.DisposeAsync(); }
        }
    }

    public async IAsyncEnumerable<Core.NormalizedChunk> NormalizeImportAsync(Core.ImportedMedia media,
        Core.NormalizationOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var manifest = JsonSerializer.Deserialize<ManagedImport>(await File.ReadAllTextAsync(media.ManagedOriginalPath + ".import.json", cancellationToken))
            ?? throw new InvalidDataException("Managed import manifest is missing.");
        if (manifest.TrackId != media.TrackId || manifest.Sha256 != media.Sha256 || manifest.Stream.Index != media.AudioStreamIndex)
            throw new InvalidDataException("Imported media differs from its managed manifest.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<Core.NormalizedChunk>(8);
        var sourceFormat = media.Probe.AudioStreams.Single(s => s.Index == media.AudioStreamIndex).Format
            ?? throw new InvalidDataException("The imported audio stream has no native format metadata.");
        var producer = Task.Run(async () =>
        {
            try
            {
                await PersistentNormalizer.NormalizeImportAsync(manifest, options.OutputDirectory, tools,
                    shard => channel.Writer.WriteAsync(shard.ToCore(sourceFormat),
                    lifetime.Token).AsTask().GetAwaiter().GetResult(), lifetime.Token,
                    options.CoreDurationSeconds, options.InitialNormalizedSample);
                channel.Writer.TryComplete();
            }
            catch (Exception exception) { channel.Writer.TryComplete(exception); }
        }, CancellationToken.None);
        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken)) yield return chunk;
            await producer;
        }
        finally { lifetime.Cancel(); await producer; }
    }

    private static Core.MediaProbe ConvertProbe(MediaProbe probe) => new(probe.Path, new FileInfo(probe.Path).Length,
        probe.DurationTicks / (double?)TimeSpan.TicksPerSecond, probe.AudioStreams.Select(s =>
        {
            var floating = s.SampleFormat.StartsWith("flt", StringComparison.Ordinal) || s.SampleFormat.StartsWith("dbl", StringComparison.Ordinal);
            var bits = s.SampleFormat.StartsWith("dbl", StringComparison.Ordinal) || s.SampleFormat.StartsWith("s64", StringComparison.Ordinal) ? 64 :
                floating || s.SampleFormat.StartsWith("s32", StringComparison.Ordinal) ? 32 : s.SampleFormat.StartsWith("u8", StringComparison.Ordinal) ? 8 : 16;
            return new Core.MediaAudioStream(s.Index, s.Codec, new(s.SampleRate, s.Channels, bits,
                floating ? Core.AudioEncoding.IeeeFloat : Core.AudioEncoding.PcmInteger), s.StartTicks / (double)TimeSpan.TicksPerSecond,
                s.DurationTicks / (double?)TimeSpan.TicksPerSecond, s.TimeBase, null, null);
        }).ToImmutableArray(), probe.ProbeVersion);
}

public sealed class AudioPlaybackService(MediaTools? tools = null) : Core.IAudioPlaybackService
{
    private readonly TimelinePlayer player = new(tools);
    private CancellationTokenSource? positionLifetime;
    private Task? positionTask;
    public event Action<Core.PlaybackPosition>? PositionChanged;
    public event Action<Exception>? PlaybackFailed { add => player.PlaybackFailed += value; remove => player.PlaybackFailed -= value; }
    public async Task PlayAsync(Core.PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        if (request.MaximumDurationTicks <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        var source = request.Source;
        var rate = source.NativeFormat?.SampleRate;
        if (rate is null)
        {
            var probe = await new MediaImporter(tools).ProbeAsync(source.Path, cancellationToken);
            rate = probe.AudioStreams.Single(s => s.Index == (source.AudioStreamIndex ?? 0)).SampleRate;
        }
        var sourceEndTicks = source.SourceFrameCount is long count
            ? checked(source.SessionStartTicks + AudioTime.FramesToTicks(count, rate.Value)) : long.MaxValue;
        var endTicks = Math.Min(sourceEndTicks, checked(request.SeekSessionTicks + request.MaximumDurationTicks));
        var frames = AudioTime.TicksToFrames(Math.Max(0, endTicks - source.SessionStartTicks), rate.Value);
        // SourceFrameOffset is track-global provenance; native archive files start at local frame zero.
        var clip = new PlaybackClip(source.Path, source.AudioStreamIndex ?? 0, source.SessionStartTicks, rate.Value, 0, frames);
        await player.PlayAsync(new(request.SeekSessionTicks, [new(source.TrackId, Volume: request.Volume, Clips: [clip])]), cancellationToken);
        positionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = positionLifetime.Token;
        positionTask = Task.Run(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (request.SeekSessionTicks + clock.Elapsed.Ticks < endTicks)
                {
                    PositionChanged?.Invoke(new(source.TrackId, request.SeekSessionTicks + clock.Elapsed.Ticks, false));
                    await Task.Delay(100, token);
                }
                await player.StopAsync();
                PositionChanged?.Invoke(new(source.TrackId, endTicks, true));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, CancellationToken.None);
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        positionLifetime?.Cancel();
        if (positionTask is not null) await positionTask;
        positionTask = null;
        positionLifetime?.Dispose(); positionLifetime = null;
        await player.StopAsync();
    }
    public async ValueTask DisposeAsync() { await StopAsync(); await player.DisposeAsync(); }
}
