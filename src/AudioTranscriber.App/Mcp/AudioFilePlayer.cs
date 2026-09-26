using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioTranscriber.App.Mcp;

/// <summary>Plays a local file to a Windows output endpoint so loopback recording can be exercised end to end.</summary>
public static class AudioFilePlayer
{
    public static async Task<object> PlayAsync(string path, string? deviceId, double volume, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Audio file not found: " + full);
        using var enumerator = new MMDeviceEnumerator();
        using var device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        await using var reader = new AudioFileReader(full) { Volume = (float)Math.Clamp(volume, 0, 1) };
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => finished.TrySetResult(e.Exception);
        output.Init(reader);
        var started = DateTime.UtcNow;
        output.Play();
        await using (cancellationToken.Register(() => output.Stop()))
        {
            var error = await finished.Task;
            if (error is not null) throw new InvalidOperationException("Playback failed: " + error.Message, error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new
        {
            path = full, device = device.FriendlyName, deviceId = device.ID,
            fileSeconds = Math.Round(reader.TotalTime.TotalSeconds, 2),
            playedSeconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 2)
        };
    }
}
