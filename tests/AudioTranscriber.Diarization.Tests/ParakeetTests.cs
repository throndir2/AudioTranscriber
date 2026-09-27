using AudioTranscriber.Core;
using Xunit;

namespace AudioTranscriber.Diarization.Tests;

public sealed class ParakeetTests
{
    [Fact]
    public void NativeResultBecomesWordTimedSegmentWithConfidence()
    {
        var provider = new SherpaParakeetProvider(Path.GetTempPath());
        // Shape of sherpa-onnx's offline result JSON: SentencePiece tokens, a leading space starts a word.
        var json = """
            {"lang": "", "text": "Yeah, uh that", "timestamps": [0.08, 0.16, 0.24, 0.40, 0.72],
             "durations": [0.08, 0.08, 0.08, 0.08, 0.16], "tokens": [" Ye", "ah", ",", " uh", " that"],
             "ys_log_probs": [-0.1, -0.2, -0.05, -2.0, -0.01], "words": []}
            """;
        var result = provider.Parse(json, 16000);
        Assert.Equal(TranscriptionStatus.Succeeded, result.Status);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(TimingGranularity.Word, segment.Timing);
        Assert.Equal(["Yeah,", "uh", "that"], segment.Words.Select(word => word.Text));
        Assert.Equal((80L, 320L), (segment.Words[0].StartMilliseconds, segment.Words[0].EndMilliseconds));
        Assert.Equal((720L, 880L), (segment.Words[2].StartMilliseconds, segment.Words[2].EndMilliseconds));
        Assert.True(segment.Words[1].Confidence < 0.2);
        var expected = new[] { -0.1, -0.2, -0.05, -2.0, -0.01 }.Select(Math.Exp).Average();
        Assert.Equal(expected, segment.Confidence!.Value, 3);
    }

    [Fact]
    public void SilenceIsEmptyAndTimesAreClampedToTheAudio()
    {
        var provider = new SherpaParakeetProvider(Path.GetTempPath());
        Assert.Equal(TranscriptionStatus.Empty,
            provider.Parse("""{"text": "", "timestamps": [], "durations": [], "tokens": [], "ys_log_probs": []}""", 16000).Status);
        var late = provider.Parse("""{"text": "hi", "timestamps": [5.0], "durations": [1.0], "tokens": [" hi"], "ys_log_probs": [0]}""", 16000);
        Assert.Equal((1000L, 1000L), (late.Segments[0].Words[0].StartMilliseconds, late.Segments[0].Words[0].EndMilliseconds));
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("en-GB", true)]
    [InlineData("de", true)]
    [InlineData("uk", true)]
    [InlineData("ja", false)]
    [InlineData("zh-CN", false)]
    public void LanguagesAreTheTwentyFiveEuropeanOnes(string language, bool supported) =>
        Assert.Equal(supported, SherpaParakeetProvider.SupportsLanguage(language));

    [Fact]
    public async Task MissingModelAndUnsupportedLanguageFailWithoutRunning()
    {
        using var directory = new TestDirectory();
        await using var provider = new SherpaParakeetProvider(directory.Path);
        var audio = System.IO.Path.Combine(directory.Path, "audio.pcm");
        await File.WriteAllBytesAsync(audio, new byte[3200]);
        var request = new TranscriptionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), audio, 1600, "en", 0);
        Assert.Equal(ProviderErrorCode.ModelUnavailable,
            (await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(request))).Error.Code);
        Assert.Equal(ProviderErrorCode.UnsupportedLanguage,
            (await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(request with { Language = "ja" }))).Error.Code);
        Assert.False(ParakeetModels.IsInstalled(directory.Path));
    }
}
