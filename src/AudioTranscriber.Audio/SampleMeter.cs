using System.Buffers.Binary;

namespace AudioTranscriber.Audio;

public static class SampleMeter
{
    public static (float Peak, float Rms) Measure(ReadOnlySpan<byte> bytes, NativeWaveFormat format)
    {
        var width = format.BitsPerSample / 8;
        var count = bytes.Length / width;
        double peak = 0, sum = 0;
        for (var i = 0; i < count; i++)
        {
            var sample = bytes.Slice(i * width, width);
            double value = (format.IsFloat, width) switch
            {
                (true, 4) => BinaryPrimitives.ReadSingleLittleEndian(sample),
                (true, 8) => BinaryPrimitives.ReadDoubleLittleEndian(sample),
                (false, 1) => (sample[0] - 128) / 128.0,
                (false, 2) => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
                (false, 3) => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608.0,
                (false, 4) => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0,
                _ => 0
            };
            if (!double.IsFinite(value)) continue;
            peak = Math.Max(peak, Math.Abs(value));
            sum += value * value;
        }
        return ((float)Math.Min(1, peak), count == 0 ? 0 : (float)Math.Min(1, Math.Sqrt(sum / count)));
    }
}
