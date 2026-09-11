using AudioTranscriber.Application;
using AudioTranscriber.Storage;
using AudioTranscriber.Core;
using Xunit;

namespace AudioTranscriber.Application.Tests;

public sealed class BoundaryTests
{
    [Fact]
    public void OwnedCoresPreserveRepeatedWordsAndZeroDurationWordsOnce()
    {
        var first = new RecognitionWindow("unused", 30 * 16000, 0, 0, 24 * 16000);
        var next = new RecognitionWindow("unused", 30 * 16000, 21 * TimeSpan.TicksPerSecond, 3 * 16000, 24 * 16000);
        var firstResult = TranscriptMerger.MergeWords(first,
            [new("yes", 23000, 23500), new("yes", 24000, 24000)], [], "Synthetic");
        var secondResult = TranscriptMerger.MergeWords(next,
            [new("yes", 2000, 2500), new("yes", 3000, 3000)], [], "Synthetic");
        Assert.Equal("yes", Assert.Single(firstResult).Text);
        Assert.Equal("yes", Assert.Single(secondResult).Text);
        Assert.Equal(24 * TimeSpan.TicksPerSecond, secondResult[0].StartTicks);
    }

    [Fact]
    public void OverlapDoesNotBecomeAConfidentNamedSpeaker()
    {
        var track = Guid.NewGuid();
        var result = TranscriptMerger.MergeWords(new("unused", 16000, 0, 0, 16000),
            [new("hello", 100, 500)],
            [new(track, 0, TimeSpan.TicksPerSecond, "speaker-1", true, false),
             new(track, 0, TimeSpan.TicksPerSecond, "speaker-2", true, false)], "Synthetic");
        Assert.Null(result[0].SpeakerId);
        Assert.True(result[0].Uncertain);
    }

    [Fact]
    public void CoarseResultsCannotSilentlyDeduplicateOverlap()
    {
        Assert.Throws<InvalidDataException>(() => TranscriptMerger.MergeSegments(
            new("unused", 30 * 16000, 0, 3 * 16000, 24 * 16000),
            [new("same words", 0, 30000, "Chunk")], [], "Synthetic"));
    }

    [Fact]
    public void InvalidProviderTimeIsNotClampedOrRelabelled()
    {
        Assert.Throws<InvalidDataException>(() => TranscriptMerger.MergeWords(
            new("unused", 16000, 0, 0, 16000), [new("word", 500, 1500)], [], "Synthetic"));
    }

    [Fact]
    public void FinalZeroDurationWordIsNotLostAtEof()
    {
        var rows = TranscriptMerger.MergeWords(new("unused", 16001, 0, 0, 16001, IncludeCoreEnd: true),
            [new("farewell", 1001, 1001)], [], "Synthetic");
        var row = Assert.Single(rows);
        Assert.Equal("farewell", row.Text);
        Assert.Equal(10_010_000, row.StartTicks);
        Assert.Equal(row.StartTicks, row.EndTicks);
    }

    [Fact]
    public void NativeClockAnchorsPreventAccumulatedMicrophoneDrift()
    {
        var track = Guid.NewGuid();
        var run = Guid.NewGuid();
        var format = new AudioFormat(48000, 2, 32, AudioEncoding.IeeeFloat);
        var sourceStart = 3L * 60 * 60 * 48000;
        var anchorTicks = TimeSpan.FromHours(3).Ticks + TimeSpan.TicksPerSecond / 2;
        var original = new NativeChunk(Guid.NewGuid(), track, "unused.wav", format, sourceStart, 30 * 48000,
            anchorTicks, run);
        var normalized = new NormalizedChunk(Guid.NewGuid(), track, "unused.pcm16",
            3L * 60 * 60 * 16000 + 16000, 24 * 16000, sourceStart + 48000, 24 * 48000,
            format, TimeSpan.FromHours(3).Ticks + TimeSpan.TicksPerSecond, run);
        var mapped = RecordedClockMapping.AtNativeAnchor(normalized, original);
        Assert.Equal(anchorTicks + TimeSpan.TicksPerSecond, mapped.SessionStartTicks);
        Assert.Equal(normalized.NormalizedStartSample, mapped.NormalizedStartSample);
        Assert.Equal(normalized.SourceFrameOffset, mapped.SourceFrameOffset);
    }
}
