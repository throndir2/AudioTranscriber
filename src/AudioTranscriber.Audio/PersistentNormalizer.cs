using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioTranscriber.Audio;

public sealed record NormalizationOptions(string Directory, Guid SessionId, Guid TrackId, long ContinuityId,
    long SourceStartFrame, long SessionStartTicks, NativeWaveFormat Format, long NormalizedStartSample = 0,
    int ShardSeconds = 24);

/// <summary>One FFmpeg resampler per continuity run, not one per WAV. CompleteAsync sends and drains EOF.</summary>
public sealed class PersistentNormalizer : IAsyncDisposable
{
    private readonly NormalizationOptions options;
    private readonly OwnedMediaProcess process;
    private readonly Task reader;
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private readonly CancellationTokenSource lifetime;
    private readonly DerivativeWriter derivatives;
    private long sourceFrames;
    private bool complete;
    private bool disposed;
    public event Action<NormalizedShard>? ShardSealed;
    public long EmittedSamples => derivatives.EmittedSamples;

    public PersistentNormalizer(NormalizationOptions options, MediaTools? tools = null, CancellationToken cancellationToken = default)
    {
        if (options.ShardSeconds is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "file,pipe",
            "-f", options.Format.FFmpegSampleFormat, "-ar", options.Format.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", options.Format.Channels.ToString(CultureInfo.InvariantCulture)
        };
        if (options.Format.ChannelMask != 0) arguments.AddRange(["-channel_layout", $"0x{options.Format.ChannelMask:x}"]);
        arguments.AddRange(["-i", "pipe:0", "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-af", "aresample=16000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"]);
        process = new OwnedMediaProcess((tools ?? new()).FFmpeg, arguments, lifetime.Token);
        derivatives = new(options, () => Interlocked.Read(ref sourceFrames), shard => ShardSealed?.Invoke(shard));
        reader = ReadOutputAsync();
    }

    public async Task AppendChunkAsync(OriginalChunk chunk, CancellationToken cancellationToken = default)
    {
        await inputGate.WaitAsync(cancellationToken);
        try
        {
            if (complete) throw new InvalidOperationException("Normalizer input is closed.");
            if (chunk.SessionId != options.SessionId || chunk.TrackId != options.TrackId ||
                chunk.ContinuityId != options.ContinuityId ||
                chunk.SourceStartFrame != checked(options.SourceStartFrame + sourceFrames) ||
                !chunk.Format.SerializedFormat.AsSpan().SequenceEqual(options.Format.SerializedFormat))
                throw new InvalidDataException("A normalizer can consume only one contiguous native-format continuity run.");
            LocalMedia.RequireFile(chunk.Path);
            await using var input = new FileStream(chunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
            if (!hash.Equals(chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Original chunk hash mismatch.");
            input.Position = chunk.DataOffset;
            var remaining = chunk.DataBytes;
            if (remaining != checked(chunk.FrameCount * chunk.Format.BlockAlign)) throw new InvalidDataException("Invalid native chunk length.");
            Interlocked.Add(ref sourceFrames, chunk.FrameCount);
            var buffer = new byte[65536];
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("Original chunk truncated.");
                await process.Input.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        catch
        {
            lifetime.Cancel();
            throw;
        }
        finally { inputGate.Release(); }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await inputGate.WaitAsync(cancellationToken);
        try
        {
            if (complete) { await reader; return; }
            complete = true;
            await process.Input.FlushAsync(cancellationToken);
            process.Input.Close();
        }
        finally { inputGate.Release(); }
        try
        {
            await reader.WaitAsync(cancellationToken);
            await process.CompleteAsync(cancellationToken);
            derivatives.CommitEnd();
        }
        catch { lifetime.Cancel(); throw; }
    }

    private async Task ReadOutputAsync()
    {
        try { await derivatives.ReadAsync(process.Output, lifetime.Token); }
        catch { lifetime.Cancel(); throw; }
    }

    /// <summary>Replay from the continuity start. Verified committed shards are compared and reused, not emitted twice.</summary>
    public static async Task<IReadOnlyList<NormalizedShard>> NormalizeChunksAsync(IEnumerable<OriginalChunk> chunks,
        string directory, MediaTools? tools = null, Action<NormalizedShard>? onShard = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<NormalizedShard>();
        PersistentNormalizer? current = null;
        long? continuity = null;
        long normalizedOffset = 0;
        try
        {
            foreach (var chunk in chunks)
            {
                if (current is null || continuity != chunk.ContinuityId)
                {
                    if (current is not null)
                    {
                        await current.CompleteAsync(cancellationToken);
                        normalizedOffset = checked(normalizedOffset + current.EmittedSamples);
                        await current.DisposeAsync();
                    }
                    continuity = chunk.ContinuityId;
                    var options = new NormalizationOptions(directory, chunk.SessionId, chunk.TrackId,
                        chunk.ContinuityId, chunk.SourceStartFrame, chunk.SessionStartTicks, chunk.Format, normalizedOffset);
                    current = new(options, tools, cancellationToken);
                    current.ShardSealed += shard => { result.Add(shard); onShard?.Invoke(shard); };
                }
                await current.AppendChunkAsync(chunk, cancellationToken);
            }
            if (current is not null) await current.CompleteAsync(cancellationToken);
            return result;
        }
        finally { if (current is not null) await current.DisposeAsync(); }
    }

    public static async Task<IReadOnlyList<NormalizedShard>> NormalizeImportAsync(ManagedImport import,
        string directory, MediaTools? tools = null, Action<NormalizedShard>? onShard = null,
        CancellationToken cancellationToken = default, int shardSeconds = 24, long initialNormalizedSample = 0)
    {
        var path = LocalMedia.RequireFile(import.ManagedPath);
        await using (var original = File.OpenRead(path))
        {
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(original, cancellationToken)), import.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Managed original no longer matches its import hash.");
        }
        var format = NativeWaveFormat.FromWaveFormat(new NAudio.Wave.WaveFormat(import.Stream.SampleRate, 16, import.Stream.Channels));
        if (shardSeconds is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(shardSeconds));
        var options = new NormalizationOptions(directory, import.SessionId, import.TrackId, 0, 0, 0, format,
            initialNormalizedSample, shardSeconds);
        var result = new List<NormalizedShard>();
        using var writer = new DerivativeWriter(options, () => long.MaxValue, shard => { result.Add(shard); onShard?.Invoke(shard); });
        await using var process = new OwnedMediaProcess((tools ?? new()).FFmpeg,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "file,pipe",
             "-format_whitelist", LocalMedia.Formats, "-i", path, "-map", $"0:{import.Stream.Index}", "-vn", "-sn", "-dn",
             "-af", "aresample=16000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"], cancellationToken);
        process.Input.Close();
        await writer.ReadAsync(process.Output, cancellationToken);
        await process.CompleteAsync(cancellationToken);
        writer.CommitEnd();
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        await process.DisposeAsync();
        try { await reader; } catch (OperationCanceledException) { } catch (IOException) { }
        derivatives.Dispose();
        lifetime.Dispose();
        inputGate.Dispose();
    }

    private sealed class DerivativeWriter : IDisposable
    {
        private readonly NormalizationOptions options;
        private readonly Func<long> sourceCount;
        private readonly Action<NormalizedShard> onShard;
        private readonly int capacity;
        private readonly byte[] buffer;
        private int filled;
        private readonly string directory;
        public long EmittedSamples { get; private set; }
        public DerivativeWriter(NormalizationOptions options, Func<long> sourceCount, Action<NormalizedShard> onShard)
        {
            this.options = options; this.sourceCount = sourceCount; this.onShard = onShard;
            capacity = checked(options.ShardSeconds * 16000 * 2);
            buffer = new byte[capacity];
            directory = Path.Combine(Path.GetFullPath(options.Directory), options.SessionId.ToString("N"),
                options.TrackId.ToString("N"), $"run-{options.ContinuityId}");
            Directory.CreateDirectory(directory);
            var runPath = Path.Combine(directory, "run.json");
            if (File.Exists(runPath))
            {
                var prior = JsonSerializer.Deserialize<NormalizationOptions>(File.ReadAllText(runPath));
                if (prior is null || prior.SourceStartFrame != options.SourceStartFrame ||
                    prior.NormalizedStartSample != options.NormalizedStartSample || prior.SessionStartTicks != options.SessionStartTicks ||
                    prior.ShardSeconds != options.ShardSeconds ||
                    !prior.Format.SerializedFormat.AsSpan().SequenceEqual(options.Format.SerializedFormat))
                    throw new InvalidDataException("Replay configuration differs from the committed continuity run.");
            }
            else LocalMedia.AtomicJson(runPath, options);
        }

        public async Task ReadAsync(Stream output, CancellationToken cancellationToken)
        {
            var incoming = new byte[65536];
            int count;
            while ((count = await output.ReadAsync(incoming, cancellationToken)) > 0)
            {
                var consumed = 0;
                while (consumed < count)
                {
                    if (filled == capacity) Seal();
                    var copy = Math.Min(capacity - filled, count - consumed);
                    incoming.AsSpan(consumed, copy).CopyTo(buffer.AsSpan(filled));
                    filled += copy;
                    consumed += copy;
                }
            }
            if ((filled & 1) != 0) throw new InvalidDataException("FFmpeg emitted a partial PCM16 sample.");
            // The tail is committed only after the child exits successfully.
        }

        public void CommitEnd()
        {
            if (filled > 0) Seal(isFinal: true);
            var endPath = Path.Combine(directory, "complete.json");
            if (File.Exists(endPath))
            {
                using var prior = JsonDocument.Parse(File.ReadAllText(endPath));
                if (prior.RootElement.GetProperty("EmittedSamples").GetInt64() != EmittedSamples)
                    throw new InvalidDataException("Continuity replay produced a different total sample count.");
            }
            LocalMedia.AtomicJson(endPath, new { EmittedSamples, SourceFrames = sourceCount(), CompletedAt = DateTimeOffset.UtcNow });
        }

        private void Seal(bool isFinal = false)
        {
            var start = EmittedSamples;
            var samples = filled / 2;
            var final = Path.Combine(directory, $"{start:D20}.pcm");
            var digest = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, filled))).ToLowerInvariant();
            var shard = new NormalizedShard(Guid.NewGuid(), options.SessionId, options.TrackId, final, options.ContinuityId,
                checked(options.NormalizedStartSample + start), samples,
                checked(options.SourceStartFrame + AudioTime.Scale(start, options.Format.SampleRate, 16000)),
                checked(options.SourceStartFrame + (isFinal && sourceCount() != long.MaxValue ? sourceCount() :
                    Math.Min(sourceCount(), AudioTime.Scale(start + samples, options.Format.SampleRate, 16000)))),
                options.Format.SampleRate, checked(options.SessionStartTicks + AudioTime.FramesToTicks(start, 16000)), digest);
            if (File.Exists(final))
            {
                using var existing = File.OpenRead(final);
                if (existing.Length != filled ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(existing)), digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Committed derivative differs from replay; original retained.");
                if (File.Exists(final + ".json"))
                {
                    var prior = JsonSerializer.Deserialize<NormalizedShard>(File.ReadAllText(final + ".json"))
                        ?? throw new InvalidDataException("Invalid derivative manifest.");
                    if (prior.Sha256 != shard.Sha256 || prior.NormalizedStartSample != shard.NormalizedStartSample ||
                        prior.SampleCount != shard.SampleCount || prior.SourceStartFrame != shard.SourceStartFrame ||
                        prior.SourceEndFrame != shard.SourceEndFrame)
                        throw new InvalidDataException("Derivative manifest differs from continuity replay.");
                    shard = prior;
                }
                else LocalMedia.AtomicJson(final + ".json", shard);
            }
            else
            {
                LocalMedia.CheckSpace(directory, filled + 16L * 1024 * 1024);
                var partial = final + ".partial";
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(buffer, 0, filled);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(partial, final);
                LocalMedia.AtomicJson(final + ".json", shard);
            }
            EmittedSamples = checked(EmittedSamples + samples);
            filled = 0;
            onShard(shard);
        }
        public void Dispose() { }
    }
}
