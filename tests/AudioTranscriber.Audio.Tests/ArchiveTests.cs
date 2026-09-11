using AudioTranscriber.Audio;
using NAudio.Wave;
using Xunit;

namespace AudioTranscriber.Audio.Tests;

public sealed class TestFiles : IDisposable
{
    public string Root { get; } = Path.GetFullPath(Path.Combine("audio-test-artifacts", Guid.NewGuid().ToString("N")));
    public TestFiles() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }

    public static async Task WriteAsync(NativeArchiveWriter writer, PooledPacketQueue queue, NativeWaveFormat format,
        long sourceFrame, int frames, long qpc, PacketFlags flags = PacketFlags.None, byte value = 7, long? deviceFrame = null)
    {
        Assert.True(queue.TryWrite(checked(frames * format.BlockAlign), sourceFrame, frames, deviceFrame ?? sourceFrame,
            qpc, flags, bytes =>
            {
                if ((flags & PacketFlags.Silent) != 0) format.FillSilence(bytes.Span);
                else bytes.Span.Fill(value);
            }));
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        using (reader.Current) writer.Write(reader.Current);
    }
}

public sealed class ArchiveTests
{
    [Fact]
    public async Task ConservesFramesAndPartialTailAcrossDurationAndByteRotationAbove32BitPositions()
    {
        using var files = new TestFiles();
        var format = NativeWaveFormat.FromWaveFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var start = (1L << 32) + 991;
        var chunks = new List<OriginalChunk>();
        using var archive = new NativeArchiveWriter(new(files.Root, Guid.NewGuid(), Guid.NewGuid(), format, 2000,
            "synthetic", true, MaxChunkBytes: 48000 * 8 / 2, MinimumFreeBytes: 0));
        using var queue = new PooledPacketQueue(1024 * 1024);
        archive.ChunkSealed += chunks.Add;
        var total = 121017;
        var written = 0;
        while (written < total)
        {
            var frames = Math.Min(4093, total - written);
            await TestFiles.WriteAsync(archive, queue, format, start + written, frames,
                2000 + AudioTime.FramesToTicks(written, format.SampleRate));
            written += frames;
        }
        archive.Finish();
        Assert.Equal(total, chunks.Sum(c => c.FrameCount));
        Assert.Equal(6, chunks.Count);
        Assert.Equal(start, chunks[0].SourceStartFrame);
        Assert.Equal(start + total, archive.DurableThroughFrame);
        Assert.InRange(chunks[^1].FrameCount, 1017, 1100);
        foreach (var chunk in chunks)
        {
            Assert.True(File.Exists(chunk.Path + ".json"));
            Assert.False(File.Exists(chunk.Path + ".partial"));
            using var wave = new WaveFileReader(chunk.Path);
            Assert.Equal(chunk.FrameCount * format.BlockAlign, wave.Length);
            Assert.All(chunk.Clocks, c => Assert.True(c.SourceFrame >= start));
            Assert.True(chunk.DataBytes <= 64L * 1024 * 1024);
            Assert.True(new FileInfo(chunk.Path).Length <= 48000 * 8 / 2);
        }
        Assert.Equal(0, queue.ReservedBytes);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(32, true)]
    public async Task SilentFlagProducesRealSilenceAndCorrectPartialSampleTail(int bits, bool floating)
    {
        using var files = new TestFiles();
        var format = NativeWaveFormat.FromWaveFormat(floating
            ? WaveFormat.CreateIeeeFloatWaveFormat(48000, 1) : new WaveFormat(48000, bits, 1));
        var chunks = new List<OriginalChunk>();
        using var archive = new NativeArchiveWriter(new(files.Root, Guid.NewGuid(), Guid.NewGuid(), format, 0,
            "synthetic", true, MinimumFreeBytes: 0));
        using var queue = new PooledPacketQueue(65536);
        archive.ChunkSealed += chunks.Add;
        await TestFiles.WriteAsync(archive, queue, format, 0, 137, 0, PacketFlags.Silent);
        archive.Finish();
        var chunk = Assert.Single(chunks);
        var bytes = File.ReadAllBytes(chunk.Path).AsSpan((int)chunk.DataOffset, (int)chunk.DataBytes).ToArray();
        Assert.All(bytes, b => Assert.Equal(bits == 8 ? (byte)128 : (byte)0, b));
        Assert.Equal((0f, 0f), SampleMeter.Measure(bytes, format));
        Assert.Equal(137, chunk.FrameCount);
    }

    [Fact]
    public async Task PoolBudgetIncludesWriterOwnedPacketsAndNeverDropsOldest()
    {
        using var queue = new PooledPacketQueue(1024);
        Assert.True(queue.TryWrite(700, 0, 350, 0, 0, PacketFlags.None, bytes => bytes.Span.Fill(71)));
        Assert.False(queue.TryWrite(2, 350, 1, 350, 1, PacketFlags.None, bytes => bytes.Span.Clear()));
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1024, queue.ReservedBytes);
        Assert.False(queue.TryWrite(2, 350, 1, 350, 1, PacketFlags.None, bytes => bytes.Span.Clear()));
        Assert.All(reader.Current.Data.ToArray(), b => Assert.Equal(71, b));
        reader.Current.Dispose();
        Assert.Equal(0, queue.ReservedBytes);
        Assert.True(queue.TryWrite(2, 350, 1, 350, 1, PacketFlags.None, bytes => bytes.Span.Clear()));
    }

    [Fact]
    public async Task RecoverySealsOnlyCheckpointedAlignedFramesAndRetainsEvidence()
    {
        using var files = new TestFiles();
        var format = NativeWaveFormat.FromWaveFormat(new WaveFormat(44100, 16, 2));
        var session = Guid.NewGuid(); var track = Guid.NewGuid();
        using var queue = new PooledPacketQueue(65536);
        using (var archive = new NativeArchiveWriter(new(files.Root, session, track, format, 0,
            "synthetic", true, DurabilityMilliseconds: 10000, MinimumFreeBytes: 0)))
        {
            await TestFiles.WriteAsync(archive, queue, format, 0, 137, 0);
            archive.Checkpoint();
            await TestFiles.WriteAsync(archive, queue, format, 137, 31, AudioTime.FramesToTicks(137, 44100));
        }
        var directory = Path.Combine(files.Root, session.ToString("N"), track.ToString("N"));
        var recovery = NativeArchiveWriter.Recover(directory);
        var chunk = Assert.Single(recovery.Chunks);
        Assert.Equal(137, chunk.FrameCount);
        Assert.True(chunk.Recovered);
        Assert.NotEmpty(recovery.Diagnostics);
        Assert.Single(Directory.GetFiles(directory, "*.partial"));
        Assert.Empty(NativeArchiveWriter.Recover(directory).Chunks);
    }

    [Fact]
    public async Task LoopbackIdleIsTimedSilenceButDiscontinuityIsUnknown()
    {
        using var files = new TestFiles();
        var format = NativeWaveFormat.FromWaveFormat(new WaveFormat(48000, 16, 1));
        var gaps = new List<AudioGap>(); var chunks = new List<OriginalChunk>();
        using var archive = new NativeArchiveWriter(new(files.Root, Guid.NewGuid(), Guid.NewGuid(), format, 0,
            "synthetic", true, MinimumFreeBytes: 0));
        archive.Gap += gaps.Add; archive.ChunkSealed += chunks.Add;
        using var queue = new PooledPacketQueue(65536);
        await TestFiles.WriteAsync(archive, queue, format, 0, 480, 0);
        await TestFiles.WriteAsync(archive, queue, format, 480, 480, TimeSpan.TicksPerSecond);
        await TestFiles.WriteAsync(archive, queue, format, 960, 480, 2 * TimeSpan.TicksPerSecond, PacketFlags.Discontinuity);
        archive.Finish(3 * TimeSpan.TicksPerSecond);
        Assert.Equal([AudioGapKind.TimedSilence, AudioGapKind.UnknownLoss, AudioGapKind.TimedSilence], gaps.Select(g => g.Kind));
        Assert.Equal(3, chunks.Select(c => c.ContinuityId).Distinct().Count());
    }

    [Fact]
    public void NativeExtensibleLayoutRoundTrips()
    {
        var original = new WaveFormatExtensible(48000, 32, 6);
        var format = NativeWaveFormat.FromWaveFormat(original);
        Assert.Equal(6, format.Channels);
        Assert.Equal(24, format.BlockAlign);
        Assert.Equal(32, format.ValidBitsPerSample);
        Assert.NotEqual(0u, format.ChannelMask);
        Assert.Equal(format, NativeWaveFormat.FromWaveFormat(format.ToWaveFormat()) with { SerializedFormat = format.SerializedFormat });
    }

    [Fact]
    public void RationalClocksDoNotOverflowIntermediateProducts() =>
        Assert.Equal((long)((Int128)long.MaxValue * 16000 / 48000), AudioTime.Scale(long.MaxValue, 16000, 48000));
}
