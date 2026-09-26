using System.Buffers.Binary;
using AudioTranscriber.Storage;
using Xunit;

namespace AudioTranscriber.Application.Tests;

public sealed class EchoReductionTests
{
    private const int Rate = 16000;

    [Fact]
    public async Task SpeakerEchoIsRemovedWhileLocalSpeechIsKept()
    {
        var directory = Directory.CreateTempSubdirectory("echo-reduction-");
        try
        {
            var random = new Random(3);
            var count = 20 * Rate;
            var speaker = new float[count];
            var local = new float[count];
            for (var i = 0; i < count; i++)
            {
                var t = i / (double)Rate;
                var syllable = Math.Max(0, Math.Sin(2 * Math.PI * 3.1 * t));
                if (t < 14) speaker[i] = (float)((random.NextDouble() * 2 - 1) * 0.3 * syllable);
                if (t >= 15) local[i] = (float)((random.NextDouble() * 2 - 1) * 0.2 * Math.Max(0, Math.Sin(2 * Math.PI * 2.3 * t)));
            }
            var delay = Rate / 10;
            var microphone = new float[count];
            for (var i = 0; i < count; i++)
            {
                double echo = 0;
                for (var k = 0; k < 400; k += 40) if (i - delay - k >= 0) echo += speaker[i - delay - k] * 0.4 * Math.Exp(-k / 120.0);
                microphone[i] = (float)echo + local[i];
            }
            var micChunk = Write(directory.FullName, "mic.pcm16", microphone);
            var speakerChunk = Write(directory.FullName, "speaker.pcm16", speaker);
            var windowPath = Path.Combine(directory.FullName, "window.pcm16");
            File.Copy(micChunk.Path, windowPath);
            var window = new RecognitionWindow(windowPath, count, 0, 0, count, true);

            Assert.True(await EchoReduction.ApplyAsync(window, [micChunk], [speakerChunk]));

            var cleaned = await EchoReduction.ReadAsync([micChunk with { Path = windowPath }], 0, count);
            Assert.True(Decibels(microphone, 4, 14) - Decibels(cleaned, 4, 14) > 20, "Echo-only audio was not reduced by 20 dB.");
            Assert.InRange(Decibels(microphone, 15.5, 20) - Decibels(cleaned, 15.5, 20), -3, 3);
        }
        finally { directory.Delete(true); }
    }

    private static StoredAudioChunk Write(string directory, string name, float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Clamp(samples[i] * 32768f, short.MinValue, short.MaxValue));
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return new(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), path, 0, samples.Length, 0, "{}");
    }

    private static double Decibels(float[] samples, double from, double to)
    {
        double sum = 0;
        for (var i = (int)(from * Rate); i < (int)(to * Rate); i++) sum += samples[i] * samples[i];
        return 10 * Math.Log10(sum / ((to - from) * Rate) + 1e-12);
    }
}
