using Xunit;

namespace AudioTranscriber.Diarization.Tests;

public sealed class CleanSpeechTests
{
    [Fact]
    public void EmbeddingIntervalsExcludeEveryOtherSpeakerAndTrimBoundaries()
    {
        LocalSpeakerTurn[] turns = [new(0, 10, 0), new(3, 5, 1), new(8, 10, 2)];
        var clean = CleanSpeech.Intervals(turns, 0);
        Assert.Equal(2, clean.Count);
        Assert.Contains(clean, t => t.StartSeconds == 0.12 && t.EndSeconds == 2.88);
        Assert.Contains(clean, t => t.StartSeconds == 5.12 && t.EndSeconds == 7.88);
        Assert.All(clean, c => Assert.DoesNotContain(turns.Where(t => t.LocalSpeaker != 0),
            t => c.StartSeconds < t.EndSeconds && c.EndSeconds > t.StartSeconds));
    }

    [Fact]
    public void FullyOverlappedTurnSuppliesNoCleanEvidence()
    {
        Assert.Empty(CleanSpeech.Intervals([new(0, 5, 0), new(0, 5, 1)], 0));
    }

    [Fact]
    public void DuplicateOrOverlappingSameSpeakerIntervalsDoNotDoubleCountEvidence()
    {
        var clean = CleanSpeech.Intervals([new(0, 2, 0), new(0, 2, 0), new(1, 3, 0)], 0);
        var interval = Assert.Single(clean);
        Assert.Equal(0.12, interval.StartSeconds);
        Assert.Equal(2.88, interval.EndSeconds);
    }

    [Fact]
    public void OnlyActualOverlappingPartsAreMarkedAmbiguous()
    {
        LocalSpeakerTurn[] turns = [new(0, 10, 0), new(3, 5, 1)];
        var parts = CleanSpeech.SplitAtOverlap(turns[0], turns).ToArray();
        Assert.Equal([(0d, 3d, false), (3d, 5d, true), (5d, 10d, false)], parts);
    }
}
