using AudioTranscriber.Storage;
using Xunit;

namespace AudioTranscriber.Storage.Tests;

public sealed class TranscriptLineTests
{
    private static readonly Guid Session = Guid.NewGuid(), Mic = Guid.NewGuid(), Output = Guid.NewGuid();

    private static TranscriptRow Row(string id, double start, double end, string text, string? speaker = "s1",
        Guid? track = null, string? correction = null) =>
        new(id, Session, track ?? Mic, (long)(start * TimeSpan.TicksPerSecond), (long)(end * TimeSpan.TicksPerSecond),
            text, correction, speaker, speaker ?? "Unknown", "Word", "test", speaker is null);

    [Fact]
    public void ConsecutiveSameSpeakerRowsMergeIntoOneLine()
    {
        var lines = TranscriptLine.Group([
            Row("a", 0, 2, " The dragon wakes."), Row("b", 2.5, 4, "It looks at you,"), Row("c", 4.2, 6, ", hungry."),
            Row("d", 6.5, 7, "I run!", "s2"),
            Row("e", 7.5, 9, "Coward."), Row("f", 20, 21, "Anyway."),
            Row("g", 21.5, 22, "Unknown one", null), Row("h", 22.5, 23, "Unknown two", null),
            Row("i", 23.5, 24, "Other track", "s1", Output),
        ]).ToList();
        Assert.Equal(["The dragon wakes. It looks at you,, hungry.", "I run!", "Coward.", "Anyway.", "Unknown one", "Unknown two", "Other track"],
            lines.Select(line => line.Text));
        Assert.Equal(["a", "b", "c"], lines[0].Rows.Select(row => row.Id));
        Assert.Equal(6 * TimeSpan.TicksPerSecond, lines[0].EndTicks);
    }

    [Fact]
    public void EditsOnlyChangeTheRowsTheyTouch()
    {
        var line = TranscriptLine.Group([Row("a", 0, 1, "Hello there."), Row("b", 1, 2, "How are you"), Row("c", 2, 3, "today?")]).Single();
        Assert.Equal([("b", "How are ya")], line.Corrections("Hello there. How are ya today?"));
        Assert.Equal([("a", "Hello there, how are you"), ("b", "")], line.Corrections("Hello there, how are you today?"));
        Assert.Equal([("c", "")], line.Corrections("Hello there. How are you"));
        Assert.Empty(line.Corrections(line.Text));
    }
}
