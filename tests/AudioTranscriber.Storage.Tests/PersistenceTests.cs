using AudioTranscriber.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AudioTranscriber.Storage.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
    private readonly LibraryStore store;
    private readonly StoredSession session;
    private readonly StoredTrack track;

    public PersistenceTests()
    {
        store = new(root);
        session = store.CreateSession("Synthetic public test", "parakeet-tdt-v3", "en");
        track = new(Guid.NewGuid(), session.Id, "Import", "Synthetic", null, 0, null);
        store.AddTrack(track);
    }

    [Fact]
    public void ConsentQueueRecoveryAndIdempotencyAreDurable()
    {
        var chunk = new StoredAudioChunk(Guid.NewGuid(), session.Id, track.Id, "synthetic.pcm16", 0, 16000, 0, "{}");
        store.AddNormalizedChunk(chunk);
        store.QueueTranscription(chunk, "test", "en", true);
        store.QueueTranscription(chunk, "test", "en", true);
        Assert.Null(store.ClaimNextJob());
        store.SetConsent(session.Id, true);
        var first = Assert.IsType<StoredJob>(store.ClaimNextJob());
        var reopened = new LibraryStore(root);
        reopened.RecoverInterruptedJobs();
        var second = Assert.IsType<StoredJob>(reopened.ClaimNextJob());
        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.False(store.CompleteJob(first, [], "{}", "test"));
        Assert.True(reopened.CompleteJob(second, [new(0, 10_000_000, "dragon", null, "Chunk", "synthetic")], "{}", "test"));
        Assert.False(reopened.CompleteJob(second, [], "{}", "test"));
        Assert.Single(reopened.GetTranscriptPage(session.Id));
        Assert.Equal(1, reopened.GetProgress(session.Id).Succeeded);
    }

    [Fact]
    public void SearchCorrectionsNamesAndPagingPreserveRawText()
    {
        store.UpsertSpeaker(new("s1", session.Id, "Speaker 1", null, "Synthetic"));
        for (var i = 0; i < 205; i++)
            store.ImportCue(session.Id, track.Id, $"cue-{i:D4}", new(i * 100L, i * 100L + 100, "the dragon", "s1", "Segment", "Synthetic"));
        store.CorrectSegment("cue-0000", "the dungeon");
        store.RenameSpeaker(session.Id, "s1", "Game master");
        var match = Assert.Single(store.GetTranscriptPage(session.Id, "dungeon"));
        Assert.Equal("the dragon", match.RawText);
        Assert.Equal("Game master", match.SpeakerName);
        Assert.Equal(204, store.GetTranscriptPage(session.Id, "dragon", limit: 1000).Count);
        var first = store.GetTranscriptPage(session.Id);
        var second = store.GetTranscriptPage(session.Id, after: new(first[^1].StartTicks, first[^1].Id));
        Assert.Equal(200, first.Count);
        Assert.Equal(5, second.Count);
        Assert.DoesNotContain(second, row => first.Any(previous => previous.Id == row.Id));
    }

    [Fact]
    public void CancellationCannotResurrectLateResults()
    {
        var chunk = new StoredAudioChunk(Guid.NewGuid(), session.Id, track.Id, "synthetic.pcm16", 4_400_000_000, 8000, 0, "{}");
        store.AddNormalizedChunk(chunk);
        store.QueueTranscription(chunk, "local-test", "en", false);
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        store.CancelJobs(session.Id);
        Assert.False(store.CompleteJob(job, [new(0, 100, "late", null, "Chunk", "Synthetic")], "{}", "test"));
        Assert.Empty(store.GetTranscriptPage(session.Id));
        Assert.Equal(4_400_000_000, store.GetChunk(chunk.Id).StartSample);
    }

    [Fact]
    public void NativeAndSpeakerStateSurviveReopenWithoutRenaming()
    {
        store.AddArchiveChunk(Guid.NewGuid(), track.Id, "native.wav", 5_000_000_000, 100, 20, "{\"format\":\"native\"}");
        store.UpsertSpeaker(new("s1", session.Id, "Speaker 1", null, "Local diarization"));
        store.RenameSpeaker(session.Id, "s1", "Alice");
        store.UpsertSpeaker(new("s1", session.Id, "Speaker 1", null, "Local diarization"));
        store.SetSpeakerRegistry(session.Id, "{\"nextOrdinal\":7}");
        store.ReplaceTurns(track.Id, 0, 100, [new(track.Id, 0, 50, "s1", false, false)]);
        var reopened = new LibraryStore(root);
        Assert.Single(reopened.GetArchiveManifests(track.Id));
        Assert.Equal("Alice", Assert.Single(reopened.GetSpeakers(session.Id)).Name);
        Assert.Equal("{\"nextOrdinal\":7}", reopened.GetSpeakerRegistry(session.Id));
        Assert.Equal("s1", Assert.Single(reopened.GetTurns(track.Id, 0, 100)).SpeakerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessingSuspensionGatesFutureJobsAfterRestart(bool canceled)
    {
        if (canceled) store.CancelJobs(session.Id);
        else store.PauseJobs(session.Id);
        var chunk = new StoredAudioChunk(Guid.NewGuid(), session.Id, track.Id, "synthetic.pcm16", 0, 16000, 0, "{}");
        store.AddNormalizedChunk(chunk);
        store.QueueTranscription(chunk, "local-test", "en", false);
        var reopened = new LibraryStore(root);
        reopened.RecoverInterruptedJobs();
        Assert.Null(reopened.ClaimNextJob());
        Assert.Equal(canceled ? "Canceled" : "Paused", reopened.GetSession(session.Id).ProcessingState);
        reopened.ResumeJobs(session.Id);
        var job = Assert.IsType<StoredJob>(reopened.ClaimNextJob());
        Assert.True(reopened.TryRenewLease(job));
        reopened.PauseJobs(session.Id);
        Assert.False(reopened.TryRenewLease(job));
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".txt")]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public async Task ExportRetainsLongTimesAndNames(string extension)
    {
        var start = TimeSpan.FromHours(123).Ticks;
        store.UpsertSpeaker(new("s1", session.Id, "Alice", null, "Synthetic"));
        store.ImportCue(session.Id, track.Id, "long-cue", new(start, start + TimeSpan.TicksPerSecond, "A dragon & a bard", "s1", "Chunk", "Synthetic"));
        var output = Path.Combine(root, "export" + extension);
        await TranscriptExporter.ExportAsync(store, session.Id, output);
        var content = await File.ReadAllTextAsync(output);
        Assert.Contains("Alice", content);
        if (extension != ".json")
        {
            Assert.Contains("123:00:00", content);
            Assert.True(File.Exists(output + ".provenance.json"));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
