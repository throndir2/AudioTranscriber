using AudioTranscriber.Audio;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace AudioTranscriber.Audio.Tests;

public sealed class SoakTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SyntheticArchiveActuallyWritesBeyondFourGiBWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("AUDIO_RUN_SOAK") != "1")
        {
            output.WriteLine("Not executed: set AUDIO_RUN_SOAK=1 for the opt-in >4 GiB real disk-write soak. No capture device is opened.");
            return;
        }
        using var files = new TestFiles();
        var format = NativeWaveFormat.FromWaveFormat(WaveFormat.CreateIeeeFloatWaveFormat(192000, 64));
        var chunks = new List<OriginalChunk>();
        using var archive = new NativeArchiveWriter(new(files.Root, Guid.NewGuid(), Guid.NewGuid(), format, 0,
            "synthetic-disk-soak", true, MinimumFreeBytes: 512L * 1024 * 1024));
        using var queue = new PooledPacketQueue(2 * 1024 * 1024);
        archive.ChunkSealed += chunks.Add;
        const long total = (1L << 32) + 1024 * 1024;
        const int packetBytes = 1024 * 1024;
        var source = 0L;
        for (long bytes = 0; bytes < total; bytes += packetBytes)
        {
            var frames = packetBytes / format.BlockAlign;
            await TestFiles.WriteAsync(archive, queue, format, source, frames,
                AudioTime.FramesToTicks(source, format.SampleRate), PacketFlags.Silent);
            source += frames;
        }
        archive.Finish();
        Assert.Equal(total, chunks.Sum(c => c.DataBytes));
        Assert.All(chunks, c => Assert.True(new FileInfo(c.Path).Length < uint.MaxValue));
        Assert.Equal(source, chunks.Sum(c => c.FrameCount));
        output.WriteLine($"Executed real synthetic disk writes: {total:N0} PCM bytes; {chunks.Count} sealed WAVs; {source:N0} conserved native frames.");
    }
}
