using AudioTranscriber.Audio;
using NAudio.Wave;

namespace AudioTranscriber.App.Mcp;

/// <summary>Plays a local file to an output device so loopback recording can be exercised end to end.</summary>
public static class AudioFilePlayer
{
    public static async Task<object> PlayAsync(string path, string? deviceId, double volume, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Audio file not found: " + full);
        var samples = await DecodeAsync(full, (float)Math.Clamp(volume, 0, 1), cancellationToken);
        var source = new ArraySampleProvider(samples);
        using var output = new AudioOutput(deviceId, source);
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.Stopped += error => finished.TrySetResult(error);
        var started = DateTime.UtcNow;
        output.Play();
        await using (cancellationToken.Register(() => { output.Stop(); finished.TrySetResult(null); }))
        {
            var error = await finished.Task;
            if (error is not null) throw new InvalidOperationException("Playback failed: " + error.Message, error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new
        {
            path = full, device = output.DeviceName, deviceId = output.DeviceId,
            fileSeconds = Math.Round(samples.Length / 96000.0, 2),
            playedSeconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 2)
        };
    }

    private static async Task<float[]> DecodeAsync(string path, float volume, CancellationToken cancellationToken)
    {
        await using var decoder = new OwnedMediaProcess("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-vn", "-ac", "2", "-ar", "48000", "-f", "f32le", "pipe:1"],
            cancellationToken);
        decoder.Input.Close();
        using var buffer = new MemoryStream();
        await decoder.Output.CopyToAsync(buffer, cancellationToken);
        await decoder.CompleteAsync(cancellationToken);
        var samples = new float[buffer.Length / 4];
        Buffer.BlockCopy(buffer.GetBuffer(), 0, samples, 0, samples.Length * 4);
        for (var i = 0; i < samples.Length; i++) samples[i] *= volume;
        return samples;
    }

    private sealed class ArraySampleProvider(float[] samples) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - position);
            Array.Copy(samples, position, buffer, offset, available);
            position += available;
            return available;
        }
    }
}
