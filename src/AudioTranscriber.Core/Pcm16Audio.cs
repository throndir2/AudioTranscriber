using System.Buffers.Binary;

namespace AudioTranscriber.Core;

public static class Pcm16Audio
{
    public const int SampleRate = 16_000;
    public const int MaximumSamples = 30 * SampleRate;

    public static async Task<byte[]> ReadAsync(
        string path, long sampleCount, CancellationToken cancellationToken = default)
    {
        ValidateCount(sampleCount);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != checked(sampleCount * 2))
            throw new InvalidDataException("Raw PCM16 byte length differs from the declared sample count.");
        var bytes = new byte[checked((int)sampleCount * 2)];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    public static async Task<byte[]> ReadRangeAsync(
        string path, long startSample, long sampleCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSample);
        ValidateCount(sampleCount);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (input.Length % 2 != 0 || checked(startSample + sampleCount) > input.Length / 2)
            throw new InvalidDataException("The requested PCM16 sample range is unavailable or unaligned.");
        input.Position = checked(startSample * 2);
        var bytes = new byte[checked((int)sampleCount * 2)];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    public static float[] ToFloatSamples(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length % 2 != 0 || pcm16.Length / 2 > MaximumSamples)
            throw new ArgumentException("PCM16 input must be sample-aligned and at most 30 seconds.", nameof(pcm16));
        var samples = new float[pcm16.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / 32768f;
        return samples;
    }

    // True when no 50 ms window reaches the RMS threshold (default about -42 dBFS): nothing worth recognizing.
    public static bool IsSilent(ReadOnlySpan<byte> pcm16, double rmsThreshold = 0.008)
    {
        const int window = SampleRate / 20;
        var limit = rmsThreshold * rmsThreshold * 32768.0 * 32768.0 * window;
        var samples = pcm16.Length / 2;
        for (var start = 0; start < samples; start += window)
        {
            var count = Math.Min(window, samples - start);
            double sum = 0;
            for (var i = start; i < start + count; i++)
            {
                double value = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2));
                sum += value * value;
            }
            if (sum * window / count >= limit) return false;
        }
        return true;
    }

    private static void ValidateCount(long sampleCount)
    {
        if (sampleCount is <= 0 or > MaximumSamples)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "Read between one sample and 30 seconds at a time.");
    }
}
