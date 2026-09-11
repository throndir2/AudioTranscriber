using AudioTranscriber.Integrations;
using Xunit;

namespace AudioTranscriber.Storage.Tests;

public sealed class WebVttTests
{
    [Fact]
    public async Task ParsesTimedVoicesWithoutInventingParticipantIdentifiers()
    {
        using var reader = new StringReader("""
            WEBVTT

            cue-1
            123:02:03.456 --> 123:02:04.789
            <v Alice>A dragon &amp; a bard</v>

            00:05.000 --> 00:06.000
            Unattributed speech
            """);
        var cues = new List<VttCue>();
        await foreach (var cue in WebVttReader.ReadAsync(reader)) cues.Add(cue);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Alice", cues[0].SpeakerLabel);
        Assert.Equal("A dragon & a bard", cues[0].Text);
        Assert.Equal(new TimeSpan(0, 123, 2, 3, 456).Ticks, cues[0].StartTicks);
        Assert.Null(cues[1].SpeakerLabel);
    }

    [Fact]
    public async Task RejectsInvalidRanges()
    {
        using var reader = new StringReader("WEBVTT\n\n00:02.000 --> 00:01.000\nbad\n");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in WebVttReader.ReadAsync(reader)) { }
        });
    }
}
