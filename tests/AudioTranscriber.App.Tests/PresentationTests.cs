using AudioTranscriber.App;
using AudioTranscriber.Storage;
using Xunit;

namespace AudioTranscriber.App.Tests;

public sealed class PresentationTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1.25", 12_500_000)]
    [InlineData("01:02", 620_000_000)]
    [InlineData("27:01:02.5", 972_625_000_000)]
    [InlineData("120:00", 72_000_000_000)]
    public void TimestampSupportsLongRecordingsWithoutDayRollover(string text, long expectedTicks)
    {
        Assert.True(TranscriptPresentation.TryTimestamp(text, out var ticks));
        Assert.Equal(expectedTicks, ticks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1:60")]
    [InlineData("1:60:00")]
    [InlineData("1:2:3:4")]
    [InlineData("00:hello")]
    [InlineData("999999999999999999999:01:00")]
    [InlineData("1e5")]
    public void TimestampRejectsInvalidInput(string text) =>
        Assert.False(TranscriptPresentation.TryTimestamp(text, out _));

    [Fact]
    public void TemplateTranscriptCanDropLineTimestamps()
    {
        var transcript = "Session 1\r\nSource language: en\r\n\r\n" +
            "[00:00:01.000 - 00:00:04.500] DM: You enter the tavern [at dusk].\r\n" +
            "[103:59:58.250 - 104:00:01.000] Speaker 2: I roll [d20].\r\n";
        Assert.Equal("Session 1\r\nSource language: en\r\n\r\nDM: You enter the tavern [at dusk].\r\nSpeaker 2: I roll [d20].\r\n",
            TranscriptPresentation.WithoutTimestamps(transcript));
    }

    [Fact]
    public void PresentationRetainsRawTextTrackAndGranularity()
    {
        var trackId = Guid.NewGuid();
        var row = new TranscriptRow("row", Guid.NewGuid(), trackId, 270_000_000, 300_000_000,
            "raw recognition", "user correction", null, "Unknown", "chunk", "NVIDIA / coarse", true);
        var item = new TranscriptItem(row, "Separate microphone");
        Assert.Equal("user correction", item.Text);
        Assert.Equal("raw recognition", item.Row.RawText);
        Assert.Equal(trackId, item.Row.TrackId);
        Assert.Equal("chunk", item.Timing);
        Assert.Equal("Uncertain / overlap", item.Attribution);
        Assert.Equal("NVIDIA / coarse", item.Provenance);
    }

    [Fact]
    public void TranscriptUpdateKeepsUnchangedLinesAndReplacesTheTail()
    {
        var session = Guid.NewGuid();
        var track = Guid.NewGuid();
        TranscriptRow Row(int index, string text) => new($"r{index}", session, track, index * 50_000_000L,
            index * 50_000_000L + 10_000_000, text, null, null, "Unknown", "segment", "local", false);
        var transcript = new TranscriptCollection();
        transcript.Update(Enumerable.Range(0, 500).Select(i => new TranscriptItem(Row(i, $"line {i}"), "Output")).ToList());
        Assert.Equal(500, transcript.Count);
        var kept = transcript[0];
        var changes = 0;
        transcript.CollectionChanged += (_, _) => changes++;
        var next = Enumerable.Range(0, 499).Select(i => new TranscriptItem(Row(i, $"line {i}"), "Output"))
            .Append(new TranscriptItem(Row(499, "line 499 corrected"), "Output"))
            .Append(new TranscriptItem(Row(500, "line 500"), "Output")).ToList();
        transcript.Update(next);
        Assert.Equal(501, transcript.Count);
        Assert.Same(kept, transcript[0]);
        Assert.Equal("line 499 corrected", transcript[499].Text);
        Assert.Equal("line 500", transcript[^1].Text);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void SmokeRequiresExplicitDataRoot() =>
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--smoke"]));

    [Fact]
    public void SmokeUsesOnlyTheSpecifiedRoot()
    {
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "app-options-test");
        var options = StartupOptions.Parse(["--data-root", path, "--smoke"]);
        Assert.True(options.Smoke);
        Assert.Equal(Path.GetFullPath(path), options.DataRoot);
    }

    [Theory]
    [InlineData("--api-key")]
    [InlineData("--capture")]
    [InlineData("--upload")]
    [InlineData("--data-root")]
    public void UnexpectedOptionsAreRejected(string option) =>
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse([option]));

    [Fact]
    public void DuplicateRootsAreRejected() =>
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--data-root", "one", "--data-root", "two"]));
}
