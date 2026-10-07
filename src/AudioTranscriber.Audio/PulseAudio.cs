using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;

namespace AudioTranscriber.Audio;

/// <summary>
/// Linux audio through the PulseAudio client API, which PipeWire (pipewire-pulse) also serves. Output loopback records
/// a sink's ".monitor" source; devices are listed with pactl. Requires libpulse0 and pulseaudio-utils (pactl).
/// </summary>
public static class PulseAudio
{
    private const string Simple = "libpulse-simple.so.0";
    private const string Pulse = "libpulse.so.0";
    internal const int FormatS16Le = 3, FormatFloat32Le = 5;
    private const int StreamPlayback = 1, StreamRecord = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec { public int Format; public uint Rate; public byte Channels; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr { public uint MaxLength, TLength, PreBuf, MinReq, FragSize; }

    [DllImport(Simple)] private static extern IntPtr pa_simple_new(string? server, string name, int direction, string? device,
        string streamName, ref SampleSpec spec, IntPtr channelMap, ref BufferAttr attributes, out int error);
    [DllImport(Simple)] private static extern int pa_simple_read(IntPtr stream, byte[] data, nuint bytes, out int error);
    [DllImport(Simple)] private static extern int pa_simple_write(IntPtr stream, byte[] data, nuint bytes, out int error);
    [DllImport(Simple)] private static extern int pa_simple_drain(IntPtr stream, out int error);
    [DllImport(Simple)] private static extern int pa_simple_flush(IntPtr stream, out int error);
    [DllImport(Simple)] private static extern ulong pa_simple_get_latency(IntPtr stream, out int error);
    [DllImport(Simple)] private static extern void pa_simple_free(IntPtr stream);
    [DllImport(Pulse)] private static extern IntPtr pa_strerror(int error);

    public const string MissingMessage =
        "PulseAudio/PipeWire client libraries were not found. Install libpulse0 and pulseaudio-utils (Debian/Ubuntu) " +
        "or pulseaudio-libs and pulseaudio-utils (Fedora), and make sure PipeWire (pipewire-pulse) or PulseAudio is running.";

    private static string Error(int code)
    {
        try { return Marshal.PtrToStringUTF8(pa_strerror(code)) ?? $"PulseAudio error {code}"; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return $"PulseAudio error {code}"; }
    }

    internal sealed record PulseDevice(string Id, string Name, string CaptureSource, bool IsDefault);

    /// <summary>Output devices (sinks, recorded through their monitor) or microphones (non-monitor sources).</summary>
    internal static IReadOnlyList<PulseDevice> Enumerate(bool loopback)
    {
        var kind = loopback ? "sinks" : "sources";
        var fallback = Pactl(loopback ? "get-default-sink" : "get-default-source")?.Trim();
        var result = new List<PulseDevice>();
        var json = Pactl($"-f json list {kind}");
        if (json is not null && json.TrimStart().StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;
                    if (!loopback && (name.EndsWith(".monitor", StringComparison.Ordinal) ||
                        item.TryGetProperty("monitor_of_sink", out var of) && of.ValueKind == JsonValueKind.String && of.GetString() is { Length: > 0 } and not "n/a"))
                        continue;
                    var monitor = loopback && item.TryGetProperty("monitor_source", out var m) && m.GetString() is { Length: > 0 } source
                        ? source : name + ".monitor";
                    result.Add(new(name, string.IsNullOrWhiteSpace(description) ? name : description!,
                        loopback ? monitor : name, name == fallback));
                }
                return result;
            }
            catch (JsonException) { result.Clear(); }
        }
        // Older pactl without JSON output: "index<TAB>name<TAB>driver<TAB>spec<TAB>state".
        foreach (var line in (Pactl($"list short {kind}") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length < 2) continue;
            var name = fields[1].Trim();
            if (!loopback && name.EndsWith(".monitor", StringComparison.Ordinal)) continue;
            result.Add(new(name, name, loopback ? name + ".monitor" : name, name == fallback));
        }
        return result;
    }

    internal static string? DeviceName(string id, bool loopback) =>
        Enumerate(loopback).FirstOrDefault(device => device.Id == id)?.Name;

    internal static string CaptureSource(string id, bool loopback) =>
        loopback ? Enumerate(true).FirstOrDefault(device => device.Id == id)?.CaptureSource ?? id + ".monitor" : id;

    private static string? Pactl(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("pactl", arguments)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>A blocking record stream in float32 at the requested rate and channel count.</summary>
    internal sealed class Recorder : IDisposable
    {
        private IntPtr stream;
        public NativeWaveFormat Format { get; }
        public Recorder(string source, int sampleRate, int channels, string streamName)
        {
            var spec = new SampleSpec { Format = FormatFloat32Le, Rate = (uint)sampleRate, Channels = (byte)channels };
            // ~20 ms fragments keep the capture latency close to WASAPI's.
            var attributes = new BufferAttr { MaxLength = uint.MaxValue, TLength = uint.MaxValue, PreBuf = uint.MaxValue,
                MinReq = uint.MaxValue, FragSize = (uint)(sampleRate / 50 * channels * 4) };
            try { stream = pa_simple_new(null, "AudioTranscriber", StreamRecord, source, streamName, ref spec, IntPtr.Zero, ref attributes, out var error);
                if (stream == IntPtr.Zero) throw new IOException($"Could not open audio source '{source}': {Error(error)}"); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { throw new IOException(MissingMessage, error); }
            Format = NativeWaveFormat.FromWaveFormat(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        }
        /// <summary>Fills the whole buffer (blocking) and returns the stream latency in microseconds.</summary>
        public ulong Read(byte[] buffer)
        {
            if (pa_simple_read(stream, buffer, (nuint)buffer.Length, out var error) < 0)
                throw new IOException("The audio source stopped delivering audio (device removed or audio server stopped): " + Error(error));
            var latency = pa_simple_get_latency(stream, out _);
            return latency == ulong.MaxValue ? 0 : latency;
        }
        public void Dispose() { if (stream != IntPtr.Zero) { pa_simple_free(stream); stream = IntPtr.Zero; } }
    }

    /// <summary>Plays a 32-bit float sample provider on a sink from a dedicated thread.</summary>
    internal sealed class Player : IDisposable
    {
        private readonly ISampleProvider source;
        private readonly IntPtr stream;
        private readonly Thread thread;
        private volatile bool stopping;
        private int stoppedRaised;
        public event Action<Exception?>? Stopped;

        public Player(string? sink, ISampleProvider source)
        {
            if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat) throw new ArgumentException("Playback needs float samples.");
            this.source = source;
            var spec = new SampleSpec { Format = FormatFloat32Le, Rate = (uint)source.WaveFormat.SampleRate, Channels = (byte)source.WaveFormat.Channels };
            var bytesPer100ms = (uint)(source.WaveFormat.AverageBytesPerSecond / 10);
            var attributes = new BufferAttr { MaxLength = uint.MaxValue, TLength = bytesPer100ms, PreBuf = uint.MaxValue,
                MinReq = uint.MaxValue, FragSize = uint.MaxValue };
            try { stream = pa_simple_new(null, "AudioTranscriber", StreamPlayback, sink, "Playback", ref spec, IntPtr.Zero, ref attributes, out var error);
                if (stream == IntPtr.Zero) throw new IOException($"Could not open audio output '{sink ?? "default"}': {Error(error)}"); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { throw new IOException(MissingMessage, error); }
            thread = new Thread(Run) { IsBackground = true, Name = "PulseAudio playback" };
        }

        public void Play() => thread.Start();

        private void Run()
        {
            Exception? failure = null;
            try
            {
                var format = source.WaveFormat;
                var samples = new float[format.SampleRate / 50 * format.Channels];
                var bytes = new byte[samples.Length * 4];
                while (!stopping)
                {
                    var read = source.Read(samples, 0, samples.Length);
                    if (read <= 0) { pa_simple_drain(stream, out _); break; }
                    Buffer.BlockCopy(samples, 0, bytes, 0, read * 4);
                    if (pa_simple_write(stream, read == samples.Length ? bytes : bytes[..(read * 4)], (nuint)(read * 4), out var error) < 0)
                        throw new IOException("Audio output failed: " + Error(error));
                }
            }
            catch (Exception error) { failure = error; }
            if (Interlocked.Exchange(ref stoppedRaised, 1) == 0) Stopped?.Invoke(failure);
        }

        public void Stop()
        {
            stopping = true;
            if (thread.IsAlive && thread != Thread.CurrentThread) thread.Join(2000);
        }

        public void Dispose()
        {
            Stop();
            if (thread.IsAlive) return;
            pa_simple_flush(stream, out _);
            pa_simple_free(stream);
        }
    }
}

/// <summary>Cross-platform output used for playback: WASAPI on Windows, PulseAudio/PipeWire on Linux.</summary>
public sealed class AudioOutput : IDisposable
{
    private readonly NAudio.CoreAudioApi.MMDeviceEnumerator? enumerator;
    private readonly NAudio.CoreAudioApi.MMDevice? device;
    private readonly NAudio.Wave.WasapiOut? wasapi;
    private readonly PulseAudio.Player? pulse;
    public string DeviceName { get; }
    public string DeviceId { get; }
    public event Action<Exception?>? Stopped;

    public AudioOutput(string? deviceId, ISampleProvider source)
    {
        if (OperatingSystem.IsWindows())
        {
            enumerator = new();
            device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            wasapi = new NAudio.Wave.WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            wasapi.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
            wasapi.Init(source);
            DeviceName = device.FriendlyName;
            DeviceId = device.ID;
        }
        else
        {
            var devices = PulseAudio.Enumerate(true);
            var chosen = deviceId is null ? devices.FirstOrDefault(d => d.IsDefault) : devices.FirstOrDefault(d => d.Id == deviceId);
            pulse = new PulseAudio.Player(deviceId, source);
            pulse.Stopped += error => Stopped?.Invoke(error);
            DeviceName = chosen?.Name ?? deviceId ?? "Default output";
            DeviceId = chosen?.Id ?? deviceId ?? "";
        }
    }

    public void Play() { wasapi?.Play(); pulse?.Play(); }
    public void Stop() { wasapi?.Stop(); pulse?.Stop(); }
    public void Dispose()
    {
        wasapi?.Dispose();
        pulse?.Dispose();
        device?.Dispose();
        enumerator?.Dispose();
    }
}
