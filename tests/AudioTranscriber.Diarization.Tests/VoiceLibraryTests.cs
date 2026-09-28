using Xunit;

namespace AudioTranscriber.Diarization.Tests;

public sealed class VoiceLibraryTests
{
    private static float[] Mix(int a, int b, double weight)
    {
        var values = new float[256];
        values[a] = (float)Math.Sqrt(1 - weight * weight);
        values[b] = (float)weight;
        return values;
    }

    [Fact]
    public void MatchesOnlyAClearlyClosestRememberedVoice()
    {
        var alice = VoiceLibrary.FromSamples([SpeakerReconcilerTests.Vector(0), Mix(0, 5, 0.2)])!;
        var bob = VoiceLibrary.FromSamples([SpeakerReconcilerTests.Vector(1)])!;
        var probe = VoiceLibrary.FromSamples([Mix(0, 7, 0.1)])!;
        Assert.Equal(0, VoiceLibrary.BestMatch(probe, [alice, bob], out var score));
        Assert.True(score > 0.9);

        var stranger = VoiceLibrary.FromSamples([SpeakerReconcilerTests.Vector(9)])!;
        Assert.Null(VoiceLibrary.BestMatch(stranger, [alice, bob], out _));

        // Nearly identical remembered voices are a tie, so nobody is named.
        var twin = VoiceLibrary.FromSamples([Mix(0, 5, 0.05)])!;
        Assert.Null(VoiceLibrary.BestMatch(probe, [alice, twin], out _));
    }

    [Fact]
    public void KeepsAFewDiverseSamplesPerSpeaker()
    {
        var samples = VoiceLibrary.SelectSamples(Enumerable.Range(0, 6).Select(SpeakerReconcilerTests.Vector));
        Assert.Equal(VoiceLibrary.MaximumSamplesPerSpeaker, samples.Count);
    }
}
