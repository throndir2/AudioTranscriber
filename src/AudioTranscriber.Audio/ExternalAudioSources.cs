using System.Collections.Concurrent;

namespace AudioTranscriber.Audio;

/// <summary>Pushes one packet of interleaved native frames (the array holds exactly <paramref name="frames"/> frames).
/// <paramref name="qpc100ns"/> is the packet's capture time on the same clock as <see cref="CaptureSession.Qpc100ns"/>.</summary>
public delegate void ExternalAudioWrite(byte[] data, int frames, long qpc100ns);

public sealed record ExternalAudioStart(Guid SessionId, Guid TrackId, long SessionQpcOrigin100ns, ExternalAudioWrite Write);

/// <summary>An audio source that is not a Windows endpoint (for example a Discord voice channel). It is recorded as the
/// session's output track through the same archive, normalization and transcription path as Windows loopback.</summary>
public interface IExternalAudioSource
{
    string Name { get; }
    NativeWaveFormat Format { get; }
    /// <summary>Starts pushing packets until <see cref="Stop"/> returns; packets must be contiguous in time.</summary>
    void Start(ExternalAudioStart start);
    /// <summary>Stops pushing; no packet is written after this returns.</summary>
    void Stop();
}

/// <summary>External sources by device ID; registered sources are offered beside the Windows output endpoints.</summary>
public static class ExternalAudioSources
{
    public const string Prefix = "external:";
    private static readonly ConcurrentDictionary<string, IExternalAudioSource> sources = new(StringComparer.Ordinal);

    public static string Register(string key, IExternalAudioSource source)
    {
        var id = Prefix + key;
        sources[id] = source;
        return id;
    }

    public static void Unregister(string id) => sources.TryRemove(id, out _);
    public static bool TryGet(string? id, out IExternalAudioSource source)
    {
        source = null!;
        return id is not null && sources.TryGetValue(id, out source!);
    }
    public static IReadOnlyList<(string Id, IExternalAudioSource Source)> All =>
        sources.Select(pair => (pair.Key, pair.Value)).OrderBy(pair => pair.Value.Name, StringComparer.CurrentCulture).ToArray();

    public static NativeWaveFormat Pcm16(int sampleRate, int channels) =>
        NativeWaveFormat.FromWaveFormat(new NAudio.Wave.WaveFormat(sampleRate, 16, channels));
}
