using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AudioTranscriber.Audio;
using NAudio.Wave;
using Xunit;

namespace AudioTranscriber.Audio.Tests;

public sealed class MediaTests
{
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task PersistentNormalizerConservesCrossChunkSamplesFlushesEofAndReplaysCommittedShards(int rate)
    {
        using var files = new TestFiles();
        var format = NativeWaveFormat.FromWaveFormat(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        var chunks = new List<OriginalChunk>();
        using var archive = new NativeArchiveWriter(new(files.Root, Guid.NewGuid(), Guid.NewGuid(), format, 0,
            "synthetic", true, MaxChunkSeconds: 1, MinimumFreeBytes: 0));
        archive.ChunkSealed += chunks.Add;
        using var queue = new PooledPacketQueue(1024 * 1024);
        var total = checked(rate * 49 + 137);
        for (var offset = 0; offset < total;)
        {
            var count = Math.Min(4111, total - offset);
            var position = offset;
            Assert.True(queue.TryWrite(count * format.BlockAlign, offset, count, offset,
                AudioTime.FramesToTicks(offset, rate), PacketFlags.None, memory =>
                {
                    var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(memory.Span);
                    for (var i = 0; i < count; i++)
                    {
                        samples[i * 2] = (float)(0.2 * Math.Sin((position + i) * 2 * Math.PI * 440 / rate));
                        samples[i * 2 + 1] = samples[i * 2];
                    }
                }));
            await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            using (reader.Current) archive.Write(reader.Current);
            offset += count;
        }
        archive.Finish();
        var normalized = await PersistentNormalizer.NormalizeChunksAsync(chunks, Path.Combine(files.Root, "normalized"));
        Assert.Equal(3, normalized.Count);
        var expected = (long)Math.Ceiling(total * 16000.0 / rate);
        Assert.InRange(normalized.Sum(s => s.SampleCount), expected - 1, expected + 1);
        Assert.Equal(0, normalized[0].NormalizedStartSample);
        Assert.Equal(384000, normalized[1].NormalizedStartSample);
        Assert.Equal(768000, normalized[2].NormalizedStartSample);
        Assert.Equal(24L * rate, normalized[0].SourceEndFrame);
        Assert.Equal(total, normalized[^1].SourceEndFrame);
        Assert.All(normalized, s => Assert.Equal(s.SampleCount * 2, new FileInfo(s.Path).Length));

        var golden = Path.Combine(files.Root, "entire.wav");
        using (var writer = new WaveFileWriter(golden, format.ToWaveFormat()))
        {
            foreach (var chunk in chunks)
            {
                using var source = new WaveFileReader(chunk.Path);
                source.CopyTo(writer);
            }
        }
        await using var ffmpeg = new OwnedMediaProcess("ffmpeg",
            ["-v", "error", "-nostdin", "-i", golden, "-af", "aresample=16000", "-ac", "1", "-f", "s16le", "pipe:1"]);
        ffmpeg.Input.Close();
        using var output = new MemoryStream();
        await ffmpeg.Output.CopyToAsync(output);
        await ffmpeg.CompleteAsync();
        Assert.Equal(output.ToArray(), normalized.SelectMany(s => File.ReadAllBytes(s.Path)).ToArray());
        var replay = await PersistentNormalizer.NormalizeChunksAsync(chunks, Path.Combine(files.Root, "normalized"));
        Assert.Equal(normalized.Select(s => s.Id), replay.Select(s => s.Id));
        Assert.Equal(normalized.Select(s => s.Sha256), replay.Select(s => s.Sha256));
    }

    [Fact]
    public async Task ImportsAreManagedReadOnlyHashedAndSelectedStreamValidated()
    {
        using var files = new TestFiles();
        var original = Path.Combine(files.Root, "source with spaces.wav");
        using (var writer = new WaveFileWriter(original, new WaveFormat(44100, 16, 1))) writer.Write(new byte[44101 * 2]);
        var importer = new MediaImporter();
        var probe = await importer.ProbeAsync(original);
        Assert.Equal(44100, Assert.Single(probe.AudioStreams).SampleRate);
        var managed = await importer.ImportAsync(Guid.NewGuid(), Guid.NewGuid(), original, 0, Path.Combine(files.Root, "managed"));
        Assert.True((File.GetAttributes(managed.ManagedPath) & FileAttributes.ReadOnly) != 0);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))).ToLowerInvariant(), managed.Sha256);
        File.Delete(original);
        var derivatives = await PersistentNormalizer.NormalizeImportAsync(managed, Path.Combine(files.Root, "normalized"));
        Assert.InRange(derivatives.Sum(s => s.SampleCount), 16000, 16002);
        await Assert.ThrowsAsync<ArgumentException>(() => importer.ImportAsync(Guid.NewGuid(), Guid.NewGuid(),
            managed.ManagedPath, 99, Path.Combine(files.Root, "bad-stream")));
        Assert.Empty(Directory.GetFiles(Path.Combine(files.Root, "bad-stream"), "*.partial"));
    }

    [Fact]
    public async Task ProbeRejectsNonAudioUrlsAndPlaylists()
    {
        using var files = new TestFiles();
        var invalid = Path.Combine(files.Root, "invalid.txt");
        File.WriteAllText(invalid, "not audio");
        var playlist = Path.Combine(files.Root, "remote.m3u");
        File.WriteAllText(playlist, "#EXTM3U\nhttps://example.invalid/secret.mp3");
        var importer = new MediaImporter();
        await Assert.ThrowsAsync<ArgumentException>(() => importer.ProbeAsync("https://example.invalid/audio.wav"));
        await Assert.ThrowsAsync<IOException>(() => importer.ProbeAsync(invalid));
        await Assert.ThrowsAsync<IOException>(() => importer.ProbeAsync(playlist));
    }

    [Fact]
    public async Task OwnedProcessCancellationActuallyTerminatesDecoder()
    {
        using var cancellation = new CancellationTokenSource();
        await using var process = new OwnedMediaProcess("ffmpeg",
            ["-v", "error", "-f", "s16le", "-ar", "16000", "-ac", "1", "-i", "pipe:0", "-f", "s16le", "pipe:1"], cancellation.Token);
        var pid = process.ProcessId;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => process.CompleteAsync(cancellation.Token));
        await process.DisposeAsync();
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task CoreNormalizerStreamsRealDerivativeChunks()
    {
        using var files = new TestFiles();
        var original = Path.Combine(files.Root, "source.wav");
        using (var writer = new WaveFileWriter(original, new WaveFormat(48000, 16, 1))) writer.Write(new byte[960138]);
        var normalizer = new FfmpegMediaNormalizer();
        var imported = await normalizer.CopyImportAsync(new(Guid.NewGuid(), Guid.NewGuid(), original, 0, Path.Combine(files.Root, "import")));
        var shards = new List<AudioTranscriber.Core.NormalizedChunk>();
        await foreach (var shard in normalizer.NormalizeImportAsync(imported, new(Path.Combine(files.Root, "normalized")))) shards.Add(shard);
        Assert.Single(shards);
        Assert.True(shards[0].SampleCount > 160000);
        Assert.Equal(imported.TrackId, shards[0].TrackId);
    }

    [Fact]
    public async Task PlaybackDecoderSeeksExactNativeFramesWithoutOpeningAnOutputDevice()
    {
        using var files = new TestFiles();
        var original = Path.Combine(files.Root, "seek.wav");
        const int rate = 48000;
        var floats = new float[rate * 5 * 2];
        for (var i = 0; i < floats.Length / 2; i++)
        {
            floats[i * 2] = (i % 997) / 997f;
            floats[i * 2 + 1] = -(i % 991) / 991f;
        }
        using (var writer = new WaveFileWriter(original, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)))
            writer.WriteSamples(floats, 0, floats.Length);
        var start = 3 * rate + 117;
        const int count = 713;
        await using var decoder = LocalPlaybackDecoder.Open(original, 0, rate, start, start + count);
        decoder.Input.Close();
        using var bytes = new MemoryStream();
        await decoder.Output.CopyToAsync(bytes);
        await decoder.CompleteAsync();
        var actual = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.ToArray()).ToArray();
        Assert.Equal(floats.AsSpan(start * 2, count * 2).ToArray(), actual);
    }

    [Fact]
    public async Task CoreNormalizerConsumesConsecutiveOriginalsWithoutResettingOffset()
    {
        using var files = new TestFiles();
        var format = NativeWaveFormat.FromWaveFormat(new WaveFormat(48000, 16, 1));
        var originals = new List<OriginalChunk>();
        using (var archive = new NativeArchiveWriter(new(files.Root, Guid.NewGuid(), Guid.NewGuid(), format, 0,
            "synthetic", true, MaxChunkSeconds: 1, MinimumFreeBytes: 0)))
        {
            archive.ChunkSealed += originals.Add;
            using var queue = new PooledPacketQueue(1024 * 1024);
            await TestFiles.WriteAsync(archive, queue, format, 0, 96013, 0, PacketFlags.Silent);
            archive.Finish();
        }
        var actual = new List<AudioTranscriber.Core.NormalizedChunk>();
        await foreach (var shard in new FfmpegMediaNormalizer().NormalizeAsync(Chunks(),
            new(Path.Combine(files.Root, "normalized"), InitialNormalizedSample: 1234567890123))) actual.Add(shard);
        var tail = Assert.Single(actual);
        Assert.Equal(1234567890123, tail.NormalizedStartSample);
        Assert.Equal(96013, tail.SourceFrameCount);
        Assert.InRange(tail.SampleCount, 32004, 32005);

        async IAsyncEnumerable<AudioTranscriber.Core.NativeChunk> Chunks()
        {
            foreach (var chunk in originals) { yield return chunk.ToCore(); await Task.Yield(); }
        }
    }

    [Fact]
    public void SpacePreflightRejectsImpossibleReserveBeforeAnyCapture()
    {
        using var files = new TestFiles();
        var format = NativeWaveFormat.FromWaveFormat(new WaveFormat(48000, 16, 1));
        Assert.Throws<IOException>(() => new NativeArchiveWriter(new(files.Root, Guid.NewGuid(), Guid.NewGuid(), format,
            0, "synthetic", true, MinimumFreeBytes: long.MaxValue)));
    }
}
