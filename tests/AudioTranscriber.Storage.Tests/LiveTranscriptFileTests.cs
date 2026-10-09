using AudioTranscriber.Storage;
using Xunit;

namespace AudioTranscriber.Storage.Tests;

public sealed class LiveTranscriptFileTests
{
    private static StoredSession Session(string name, DateTime localStart) =>
        new(Guid.NewGuid(), name, "", new DateTimeOffset(localStart).ToUniversalTime(), "Recording", false, "p", "en", null, 0);

    [Fact]
    public void DefaultSessionNameIsTheFileName()
    {
        var start = new DateTime(2026, 10, 9, 14, 5, 30, DateTimeKind.Local);
        Assert.Equal("Session 2026-10-09 14-05.txt", LiveTranscriptFile.FileName(Session("Session 2026-10-09 14:05", start)));
    }

    [Fact]
    public void CustomNameGetsTheStartTime()
    {
        var start = new DateTime(2026, 10, 9, 14, 5, 30, DateTimeKind.Local);
        Assert.Equal("Standup 2026-10-09 14-05.txt", LiveTranscriptFile.FileName(Session("Standup", start)));
    }

    [Fact]
    public void FolderSettingGetsOneFilePerSessionAndFileSettingStaysFixed()
    {
        var session = Session("Standup", new DateTime(2026, 10, 9, 14, 5, 0, DateTimeKind.Local));
        var folder = Path.Combine(Path.GetTempPath(), "Live transcripts");
        Assert.Equal(Path.Combine(folder, "Standup 2026-10-09 14-05.txt"), LiveTranscriptFile.TargetPath(folder, session));
        var file = Path.Combine(Path.GetTempPath(), "fixed-live.txt");
        Assert.Equal(file, LiveTranscriptFile.TargetPath(file, session));
    }
}
