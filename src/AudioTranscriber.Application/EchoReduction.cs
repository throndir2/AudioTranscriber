using System.Buffers.Binary;
using AudioTranscriber.Storage;
using SoundFlow.Extensions.WebRtc.Apm;

namespace AudioTranscriber.Application;

/// <summary>
/// Acoustic echo cancellation for the microphone track: the loopback track is exactly what the speakers played,
/// so WebRTC's AEC3 models the speaker-to-mic path and subtracts it before recognition. Originals are untouched.
/// </summary>
public static class EchoReduction
{
    private const int Rate = 16000;
    private const int Frame = Rate / 100;
    private const long TicksPerSample = TimeSpan.TicksPerSecond / Rate;
    private const float SilenceThreshold = 1f / 32768f;
    // Audio before the window lets the adaptive filter converge before the samples that are transcribed.
    public const int PreRollSamples = 5 * Rate;
    // Feeding speaker audio slightly early tolerates small clock-anchor error; AEC3 covers roughly 0-500 ms of echo delay.
    private const long ReferenceLeadTicks = 30 * TimeSpan.TicksPerMillisecond;

    /// <summary>Rewrites the PCM16 window in place. Returns false (unchanged) when the speakers were silent.</summary>
    public static async Task<bool> ApplyAsync(RecognitionWindow window, IReadOnlyList<StoredAudioChunk> microphone,
        IReadOnlyList<StoredAudioChunk> speaker, CancellationToken cancellationToken = default)
    {
        var start = checked(window.SessionStartTicks - PreRollSamples * TicksPerSample);
        var total = PreRollSamples + window.SampleCount;
        var reference = await ReadAsync(speaker, start + ReferenceLeadTicks, total, cancellationToken);
        if (!reference.Any(sample => Math.Abs(sample) >= SilenceThreshold)) return false;
        var mic = new float[total];
        (await ReadAsync(microphone, start, PreRollSamples, cancellationToken)).CopyTo(mic, 0);
        var bytes = await File.ReadAllBytesAsync(window.Path, cancellationToken);
        if (bytes.Length != window.SampleCount * 2L)
            throw new InvalidDataException("The recognition window does not match its PCM16 sample count.");
        for (var i = 0; i < window.SampleCount; i++)
            mic[PreRollSamples + i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)) / 32768f;
        var cleaned = Cancel(mic, reference);
        for (var i = 0; i < window.SampleCount; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2),
                (short)Math.Clamp(MathF.Round(cleaned[PreRollSamples + i] * 32768f), short.MinValue, short.MaxValue));
        var temporary = window.Path + ".echo.partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, window.Path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return true;
    }

    /// <summary>Removes <paramref name="speaker"/> echo from time-aligned 16 kHz mono microphone samples.</summary>
    public static float[] Cancel(ReadOnlySpan<float> microphone, ReadOnlySpan<float> speaker)
    {
        if (microphone.Length != speaker.Length) throw new ArgumentException("Microphone and speaker audio must be the same length.");
        var output = new float[microphone.Length];
        using var apm = new AudioProcessingModule();
        using var config = new ApmConfig();
        using var stream = new StreamConfig(Rate, 1);
        config.SetEchoCanceller(true, false);
        config.SetHighPassFilter(true);
        config.SetNoiseSuppression(false, NoiseSuppressionLevel.Low);
        config.SetGainController1(false, GainControlMode.AdaptiveDigital, 3, 9, false);
        config.SetGainController2(false);
        config.SetPreAmplifier(false, 1f);
        Check(apm.ApplyConfig(config));
        Check(apm.Initialize());
        float[][] render = [new float[Frame]], renderOut = [new float[Frame]];
        float[][] capture = [new float[Frame]], captureOut = [new float[Frame]];
        for (var start = 0; start < microphone.Length; start += Frame)
        {
            var count = Math.Min(Frame, microphone.Length - start);
            Array.Clear(render[0]);
            Array.Clear(capture[0]);
            speaker.Slice(start, count).CopyTo(render[0]);
            microphone.Slice(start, count).CopyTo(capture[0]);
            Check(apm.ProcessReverseStream(render, stream, stream, renderOut));
            apm.SetStreamDelayMs(0);
            Check(apm.ProcessStream(capture, stream, stream, captureOut));
            captureOut[0].AsSpan(0, count).CopyTo(output.AsSpan(start));
        }
        return output;
    }

    /// <summary>Reads a track's normalized audio for a session-time range; gaps between chunks are silence.</summary>
    public static async Task<float[]> ReadAsync(IReadOnlyList<StoredAudioChunk> chunks, long startTicks, int count,
        CancellationToken cancellationToken = default)
    {
        var result = new float[count];
        var endTicks = checked(startTicks + count * TicksPerSample);
        foreach (var chunk in chunks)
        {
            if (chunk.StartTicks + chunk.SampleCount * TicksPerSample <= startTicks || chunk.StartTicks >= endTicks) continue;
            var offset = (long)Math.Round((chunk.StartTicks - startTicks) / (double)TicksPerSample);
            var from = (int)Math.Max(0, -offset);
            var to = (int)Math.Min(chunk.SampleCount, count - offset);
            if (to <= from) continue;
            await using var input = new FileStream(chunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65_536, FileOptions.Asynchronous);
            if (input.Length != chunk.SampleCount * 2L)
                throw new InvalidDataException("A normalized chunk does not match its committed PCM16 sample count.");
            input.Position = from * 2L;
            var buffer = new byte[(to - from) * 2];
            await input.ReadExactlyAsync(buffer, cancellationToken);
            for (var i = 0; i < to - from; i++)
                result[offset + from + i] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2)) / 32768f;
        }
        return result;
    }

    private static void Check(ApmError error)
    {
        if (error != ApmError.NoError) throw new InvalidOperationException($"The WebRTC echo canceller reported {error}.");
    }
}
