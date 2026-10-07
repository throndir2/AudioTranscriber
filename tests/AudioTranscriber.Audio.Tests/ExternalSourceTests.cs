using AudioTranscriber.Audio;
using Xunit;

namespace AudioTranscriber.Audio.Tests;

public sealed class ExternalSourceTests
{
    private sealed class ToneSource : IExternalAudioSource
    {
        private Thread? thread;
        private volatile bool running;
        public string Name => "Synthetic Discord channel";
        public NativeWaveFormat Format { get; } = ExternalAudioSources.Pcm16(48_000, 2);
        public int FramesWritten;
        public void Start(ExternalAudioStart start)
        {
            running = true;
            thread = new Thread(() =>
            {
                var origin = CaptureSession.Qpc100ns();
                var packet = new byte[960 * 4];
                for (var i = 0; i < packet.Length; i += 2) { packet[i] = 0x00; packet[i + 1] = 0x10; }
                for (var frame = 0; running && frame < 100; frame++)
                {
                    start.Write(packet, 960, origin + frame * TimeSpan.TicksPerSecond / 50);
                    FramesWritten += 960;
                }
            });
            thread.Start();
        }
        public void Stop() { running = false; thread?.Join(); }
    }

    [Fact]
    public async Task AnExternalSourceIsArchivedAsTheOutputTrack()
    {
        using var files = new TestFiles();
        var source = new ToneSource();
        var id = ExternalAudioSources.Register("test:" + Guid.NewGuid().ToString("N"), source);
        try
        {
            var chunks = new List<OriginalChunk>();
            var track = Guid.NewGuid();
            var capture = new CaptureSession(new(files.Root, Guid.NewGuid(), new(track, id, true), MinimumFreeBytes: 0));
            capture.ChunkSealed += chunk => { lock (chunks) chunks.Add(chunk); };
            await capture.StartAsync();
            Assert.Equal("Synthetic Discord channel", capture.Tracks.Single().Name);
            while (source.FramesWritten < 96_000) await Task.Delay(10);
            await capture.DisposeAsync();
            Assert.Equal(96_000, chunks.Sum(chunk => chunk.FrameCount));
            Assert.All(chunks, chunk => Assert.Equal(track, chunk.TrackId));
            Assert.Equal(RecordingState.Recorded, capture.State);
        }
        finally { ExternalAudioSources.Unregister(id); }
    }
}
