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
        Assert.Equal(200, TranscriptPresentation.PageSize);
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
