using Microsoft.Data.Sqlite;
using Xunit;

namespace AudioTranscriber.Storage.Tests;

public sealed class DiarizationPersistenceTests : IDisposable
{
    private const string Provider = "local-diarization";
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestData", Guid.NewGuid().ToString("N"));
    private readonly LibraryStore store;
    private readonly StoredSession session;
    private readonly StoredTrack track;
    private int chunkIndex;

    public DiarizationPersistenceTests()
    {
        store = new(root);
        session = store.CreateSession("Synthetic speaker persistence", "local-asr", "en");
        track = new(Guid.NewGuid(), session.Id, "Import", "Synthetic", null, 0, null);
        store.AddTrack(track);
    }

    [Fact]
    public void DiarizationCompletionIsLeaseFencedDurableAndPreservesNames()
    {
        var chunk = QueueChunk();
        var first = Assert.IsType<StoredJob>(store.ClaimNextJob());
        store.UpsertSpeaker(new("s1", session.Id, "Speaker 1", null, "Synthetic"));
        store.RenameSpeaker(session.Id, "s1", "Alice");
        store.SetSpeakerRegistry(session.Id, "{\"revision\":0}");
        var original = new StoredTurn(track.Id, 0, 50, "s1", false, false);
        var outside = new StoredTurn(track.Id, 150, 175, "s1", false, false);
        store.ReplaceTurns(track.Id, 0, 200, [original, outside]);
        Assert.False(store.HasCompletedJob(chunk.Id, Provider));

        var reopened = new LibraryStore(root);
        reopened.RecoverInterruptedJobs();
        var current = Assert.IsType<StoredJob>(reopened.ClaimNextJob());
        var speakers = new StoredSpeaker[]
        {
            new("s1", session.Id, "Speaker 1", "participant-1", "Synthetic"),
            new("s2", session.Id, "Speaker 2", null, "Synthetic")
        };
        var replacement = new StoredTurn(track.Id, 5, 95, "s2", false, true);
        Assert.False(store.CompleteDiarizationJob(first, "{\"revision\":1}", speakers, [replacement], 0, 100));
        Assert.Equal("{\"revision\":0}", reopened.GetSpeakerRegistry(session.Id));
        Assert.Equal(new[] { original, outside }, reopened.GetTurns(track.Id, 0, 200));

        Assert.True(reopened.CompleteDiarizationJob(current, "{\"revision\":1}", speakers, [replacement], 0, 100));
        Assert.False(reopened.CompleteDiarizationJob(current, "{\"revision\":2}", [], [], 0, 100));

        var restarted = new LibraryStore(root);
        Assert.True(restarted.HasCompletedJob(chunk.Id, Provider));
        Assert.False(restarted.HasCompletedJob(chunk.Id, "local-asr"));
        Assert.False(restarted.HasCompletedJob(Guid.NewGuid(), Provider));
        Assert.Equal("{\"revision\":1}", restarted.GetSpeakerRegistry(session.Id));
        Assert.Equal(new[] { replacement, outside }, restarted.GetTurns(track.Id, 0, 200));
        var alice = Assert.Single(restarted.GetSpeakers(session.Id), speaker => speaker.Id == "s1");
        Assert.Equal("Alice", alice.Name);
        Assert.Equal("participant-1", alice.ParticipantId);
        Assert.Equal(2, restarted.GetSpeakers(session.Id).Count);
        Assert.Empty(restarted.GetTranscriptPage(session.Id));
        restarted.QueueTranscription(chunk, Provider, "en", false);
        Assert.Single(restarted.GetJobs(session.Id));
        Assert.Null(restarted.ClaimNextJob());
        Assert.Equal(1, restarted.GetProgress(session.Id).Succeeded);
    }

    [Fact]
    public async Task ConcurrentDiarizationCompletionCommitsExactlyOneConsistentResult()
    {
        QueueChunk();
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            var writer = new LibraryStore(root);
            await start.Task;
            var speakerId = $"speaker-{index}";
            return writer.CompleteDiarizationJob(job, $"{{\"winner\":{index}}}",
                [new(speakerId, session.Id, speakerId, null, "Synthetic")],
                [new(track.Id, 0, 100, speakerId, false, false)], 0, 100);
        })).ToArray();
        start.SetResult();

        var completed = await Task.WhenAll(attempts);
        Assert.Single(completed, accepted => accepted);
        var winner = Array.IndexOf(completed, true);
        Assert.Equal($"{{\"winner\":{winner}}}", store.GetSpeakerRegistry(session.Id));
        Assert.Equal($"speaker-{winner}", Assert.Single(store.GetSpeakers(session.Id)).Id);
        Assert.Equal($"speaker-{winner}", Assert.Single(store.GetTurns(track.Id, 0, 100)).SpeakerId);
        Assert.Equal("Succeeded", Assert.Single(store.GetJobs(session.Id)).State);
    }

    [Fact]
    public void DiarizationFailureRollsBackJobRegistrySpeakersAndTurns()
    {
        var chunk = QueueChunk();
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        store.SetSpeakerRegistry(session.Id, "{\"revision\":0}");
        store.UpsertSpeaker(new("existing", session.Id, "Alice", null, "Synthetic"));
        var original = new StoredTurn(track.Id, 0, 100, "existing", false, false);
        store.ReplaceTurns(track.Id, 0, 100, [original]);
        ExecuteSql("""
            CREATE TRIGGER reject_turn BEFORE INSERT ON turns WHEN new.speaker_id='reject'
            BEGIN SELECT RAISE(ABORT,'synthetic turn failure'); END;
            """);
        var speakers = new StoredSpeaker[]
        {
            new("existing", session.Id, "Speaker 1", "participant-1", "Synthetic"),
            new("new", session.Id, "Speaker 2", null, "Synthetic")
        };
        var turns = new StoredTurn[]
        {
            new(track.Id, 0, 50, "new", false, false),
            new(track.Id, 50, 100, "reject", true, true)
        };

        var error = Assert.Throws<SqliteException>(() =>
            store.CompleteDiarizationJob(job, "{\"revision\":1}", speakers, turns, 0, 100));
        Assert.Contains("synthetic turn failure", error.Message);
        Assert.False(store.HasCompletedJob(chunk.Id, Provider));
        var running = Assert.Single(store.GetJobs(session.Id));
        Assert.Equal("Running", running.State);
        Assert.Equal(job.LeaseToken, running.LeaseToken);
        Assert.Equal("{\"revision\":0}", store.GetSpeakerRegistry(session.Id));
        Assert.Equal(original, Assert.Single(store.GetTurns(track.Id, 0, 100)));
        Assert.Null(Assert.Single(store.GetSpeakers(session.Id)).ParticipantId);

        ExecuteSql("DROP TRIGGER reject_turn");
        Assert.True(store.CompleteDiarizationJob(job, "{\"revision\":1}", speakers, turns, 0, 100));
        Assert.True(store.HasCompletedJob(chunk.Id, Provider));
    }

    [Theory]
    [InlineData("Canceled")]
    [InlineData("Paused")]
    [InlineData("Expired")]
    public void DiarizationCompletionRejectsRevokedOrExpiredLeases(string state)
    {
        var chunk = QueueChunk();
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        store.SetSpeakerRegistry(session.Id, "{\"revision\":0}");
        if (state == "Canceled") store.CancelJobs(session.Id);
        else if (state == "Paused") store.PauseJobs(session.Id);
        else ExecuteSql("UPDATE jobs SET lease_until=0 WHERE id=$id", ("$id", job.Id));

        Assert.False(store.CompleteDiarizationJob(job, "{\"revision\":1}",
            [new("s1", session.Id, "Speaker 1", null, "Synthetic")],
            [new(track.Id, 0, 100, "s1", false, false)], 0, 100));
        Assert.False(store.HasCompletedJob(chunk.Id, Provider));
        Assert.Equal("{\"revision\":0}", store.GetSpeakerRegistry(session.Id));
        Assert.Empty(store.GetSpeakers(session.Id));
        Assert.Empty(store.GetTurns(track.Id, 0, 100));
        Assert.Equal(state == "Expired" ? "Running" : state, Assert.Single(store.GetJobs(session.Id)).State);
    }

    [Fact]
    public void DiarizationCompletionRejectsForeignOwnershipAndInvalidIntervals()
    {
        QueueChunk();
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        Assert.Throws<InvalidDataException>(() => store.CompleteDiarizationJob(job, "{}",
            [new("s1", Guid.NewGuid(), "Other session", null, "Synthetic")], [], 0, 100));
        Assert.Throws<InvalidDataException>(() => store.CompleteDiarizationJob(job, "{}", [],
            [new(Guid.NewGuid(), 0, 100, null, false, true)], 0, 100));
        Assert.Throws<InvalidDataException>(() => store.CompleteDiarizationJob(job, "{}", [],
            [new(track.Id, 0, 101, null, false, true)], 0, 100));
        Assert.Throws<InvalidDataException>(() => store.CompleteDiarizationJob(job, "{}", [], [], -1, 100));
        Assert.Throws<InvalidDataException>(() => store.CompleteDiarizationJob(job, "{}", [], [], 100, 99));
        Assert.False(store.CompleteDiarizationJob(job with { TrackId = Guid.NewGuid() }, "{}", [], [], 0, 100));
        Assert.Equal("Running", Assert.Single(store.GetJobs(session.Id)).State);
        Assert.Null(store.GetSpeakerRegistry(session.Id));
        Assert.Empty(store.GetSpeakers(session.Id));
        Assert.Empty(store.GetTurns(track.Id, 0, 100));
    }

    [Fact]
    public void AutomaticAssignmentsPreserveManualNamesAndExplicitUnknownAfterRestart()
    {
        foreach (var id in new[] { "manual", "automatic", "unknown" })
            store.ImportCue(session.Id, track.Id, id, new(0, 100, "raw dragon", null, "Word", "Synthetic", true));
        store.CorrectSegment("automatic", "corrected dungeon");
        store.AssignSpeaker("manual", "chosen");
        store.AssignSpeaker("unknown", null);
        store.ApplyAutomaticSpeakerAssignments([("manual", "automatic-id", true), ("automatic", "automatic-id", true),
            ("unknown", "automatic-id", true)]);
        var reopened = new LibraryStore(root);
        reopened.ApplyAutomaticSpeakerAssignments([("manual", null, true), ("automatic", "refined-id", false),
            ("unknown", "refined-id", true)]);

        var rows = reopened.GetTranscriptPage(session.Id).ToDictionary(row => row.Id);
        Assert.Equal("chosen", rows["manual"].SpeakerId);
        Assert.False(rows["manual"].Uncertain);
        Assert.Null(rows["unknown"].SpeakerId);
        Assert.False(rows["unknown"].Uncertain);
        Assert.Equal("refined-id", rows["automatic"].SpeakerId);
        Assert.False(rows["automatic"].Uncertain);
        Assert.Equal("raw dragon", rows["automatic"].RawText);
        Assert.Equal("corrected dungeon", rows["automatic"].Correction);
        Assert.Equal("automatic", Assert.Single(reopened.GetTranscriptPage(session.Id, "dungeon")).Id);
    }

    [Fact]
    public void AutomaticAssignmentBatchRollsBackOnFailure()
    {
        foreach (var id in new[] { "first", "second" })
            store.ImportCue(session.Id, track.Id, id, new(0, 100, "raw", null, "Word", "Synthetic", true));
        ExecuteSql("""
            CREATE TRIGGER reject_assignment BEFORE UPDATE OF speaker_id ON segments WHEN old.id='second'
            BEGIN SELECT RAISE(ABORT,'synthetic assignment failure'); END;
            """);

        Assert.Throws<SqliteException>(() => store.ApplyAutomaticSpeakerAssignments(
            [("first", "s1", false), ("second", "s2", false)]));
        Assert.All(store.GetTranscriptPage(session.Id), row =>
        {
            Assert.Null(row.SpeakerId);
            Assert.True(row.Uncertain);
        });
    }

    [Fact]
    public async Task VersionOneMigrationPreservesLegacyChoicesAndAllowsNewAutomaticAssignments()
    {
        store.ImportCue(session.Id, track.Id, "legacy-name", new(0, 100, "legacy dragon", null, "Word", "Synthetic"));
        store.ImportCue(session.Id, track.Id, "legacy-unknown", new(100, 200, "legacy dragon", null, "Word", "Synthetic"));
        store.AssignSpeaker("legacy-name", "chosen");
        store.AssignSpeaker("legacy-unknown", null);
        store.CorrectSegment("legacy-name", "legacy dungeon");
        ExecuteSql("DROP TABLE job_attempts; ALTER TABLE segments DROP COLUMN manual_speaker; ALTER TABLE sessions DROP COLUMN processing_state; PRAGMA user_version=1;");

        var stores = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new LibraryStore(root))));
        var reopened = stores[0];
        reopened.ImportCue(session.Id, track.Id, "new", new(200, 300, "new dragon", null, "Word", "Synthetic", true));
        reopened.ApplyAutomaticSpeakerAssignments([("legacy-name", "auto", true), ("legacy-unknown", "auto", true),
            ("new", "auto", false)]);

        var rows = reopened.GetTranscriptPage(session.Id).ToDictionary(row => row.Id);
        Assert.Equal("chosen", rows["legacy-name"].SpeakerId);
        Assert.Null(rows["legacy-unknown"].SpeakerId);
        Assert.Equal("auto", rows["new"].SpeakerId);
        Assert.Equal("legacy-name", Assert.Single(reopened.GetTranscriptPage(session.Id, "dungeon")).Id);
        Assert.Equal(4L, ScalarSql("PRAGMA user_version"));
        Assert.Equal(3, new LibraryStore(root).GetTranscriptPage(session.Id).Count);
    }

    [Fact]
    public void NewerSchemaVersionIsRejectedWithoutDowngrading()
    {
        ExecuteSql("PRAGMA user_version=5");
        Assert.Throws<InvalidDataException>(() => new LibraryStore(root));
        Assert.Equal(5L, ScalarSql("PRAGMA user_version"));
    }

    [Fact]
    public void ProviderResumeIsExplicitAndScopedWithoutResettingAttempts()
    {
        var states = new[] { "Paused", "Blocked", "Failed", "Pending", "RetryWaiting", "Running", "Succeeded", "Canceled" };
        var jobs = new Dictionary<Guid, string>();
        foreach (var state in states)
        {
            var chunk = QueueChunk();
            var job = Assert.Single(store.GetJobs(session.Id), candidate => candidate.ChunkId == chunk.Id);
            jobs.Add(job.Id, state);
            ExecuteSql("""
                UPDATE jobs SET state=$state,attempts=7,error='synthetic',next_attempt=9999999999999,
                    lease='preserved-lease',lease_until=9999999999999 WHERE id=$id
                """, ("$state", state), ("$id", job.Id));
        }
        var otherProviderChunk = QueueChunk("other-provider");
        ExecuteSql("UPDATE jobs SET state='Blocked' WHERE chunk_id=$chunk", ("$chunk", otherProviderChunk.Id));
        var otherSession = store.CreateSession("Other session", Provider, "en");
        var otherTrack = new StoredTrack(Guid.NewGuid(), otherSession.Id, "Import", "Synthetic", null, 0, null);
        store.AddTrack(otherTrack);
        var otherChunk = new StoredAudioChunk(Guid.NewGuid(), otherSession.Id, otherTrack.Id, "synthetic.pcm16", 0, 16000, 0, "{}");
        store.AddNormalizedChunk(otherChunk);
        store.QueueTranscription(otherChunk, Provider, "en", false);
        ExecuteSql("UPDATE jobs SET state='Blocked' WHERE chunk_id=$chunk", ("$chunk", otherChunk.Id));

        store.ResumeProviderJobs(session.Id, Provider);

        foreach (var job in store.GetJobs(session.Id).Where(job => jobs.ContainsKey(job.Id)))
        {
            var resumes = jobs[job.Id] is "Paused" or "Blocked" or "Failed";
            Assert.Equal(resumes ? "Pending" : jobs[job.Id], job.State);
            Assert.Equal(7, job.Attempts);
            Assert.Equal(resumes ? null : "synthetic", job.Error);
            Assert.Equal(resumes ? null : "preserved-lease", job.LeaseToken);
            Assert.Equal(resumes ? 0L : 9999999999999L, ScalarSql("SELECT next_attempt FROM jobs WHERE id=$id", ("$id", job.Id)));
        }
        Assert.Equal("Blocked", Assert.Single(store.GetJobs(session.Id), job => job.ChunkId == otherProviderChunk.Id).State);
        Assert.Equal("Blocked", Assert.Single(store.GetJobs(otherSession.Id)).State);
    }

    [Fact]
    public void RawAttemptsSurviveFailedMergeRetryAndRestartWithoutOverwritingEvidence()
    {
        var chunk = QueueChunk("local-asr");
        var first = Assert.IsType<StoredJob>(store.ClaimNextJob());
        const string invalidResponse = "{\"text\":\"dragon\",\"startTicks\":-1}";
        store.SaveRawAttempt(first, invalidResponse, "observed-model-1");
        Assert.Throws<InvalidDataException>(() => store.CompleteJob(first,
            [new(-1, 100, "dragon", null, "Word", "Synthetic")], invalidResponse, "observed-model-1"));
        store.FailJob(first, "Invalid provider timing");

        var reopened = new LibraryStore(root);
        Assert.Equal(invalidResponse, ScalarSql("SELECT raw_json FROM job_attempts WHERE job_id=$id AND attempt=1", ("$id", first.Id)));
        Assert.Equal("observed-model-1", ScalarSql("SELECT model FROM job_attempts WHERE job_id=$id AND attempt=1", ("$id", first.Id)));
        Assert.Equal(0L, ScalarSql("SELECT count(*) FROM results WHERE job_id=$id", ("$id", first.Id)));
        Assert.False(reopened.HasCompletedJob(chunk.Id, "local-asr"));
        reopened.ResumeJobs(session.Id);
        var second = Assert.IsType<StoredJob>(reopened.ClaimNextJob());
        Assert.Equal(2, second.Attempts);
        const string validResponse = "{\"text\":\"dragon\",\"startTicks\":0}";
        reopened.SaveRawAttempt(second, validResponse, null);
        Assert.True(reopened.CompleteJob(second, [new(0, 100, "dragon", null, "Word", "Synthetic")],
            validResponse, "advertised-model"));
        reopened.SaveRawAttempt(first, "{\"different\":\"duplicate response\"}", "different-model");

        var restarted = new LibraryStore(root);
        Assert.True(restarted.HasCompletedJob(chunk.Id, "local-asr"));
        Assert.Equal(2L, ScalarSql("SELECT count(*) FROM job_attempts WHERE job_id=$id", ("$id", first.Id)));
        Assert.Equal(invalidResponse, ScalarSql("SELECT raw_json FROM job_attempts WHERE job_id=$id AND attempt=1", ("$id", first.Id)));
        Assert.Equal(validResponse, ScalarSql("SELECT raw_json FROM job_attempts WHERE job_id=$id AND attempt=2", ("$id", first.Id)));
        Assert.Equal(DBNull.Value, ScalarSql("SELECT model FROM job_attempts WHERE job_id=$id AND attempt=2", ("$id", first.Id)));
        Assert.Equal(validResponse, ScalarSql("SELECT raw_json FROM results WHERE job_id=$id", ("$id", first.Id)));
        Assert.Single(restarted.GetTranscriptPage(session.Id));
    }

    [Fact]
    public void LateRawEvidenceDoesNotResurrectCanceledWork()
    {
        var chunk = QueueChunk();
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        store.CancelJobs(session.Id);
        store.SaveRawAttempt(job, "{\"completedResponse\":true}", "observed");

        var reopened = new LibraryStore(root);
        Assert.Equal(1L, ScalarSql("SELECT count(*) FROM job_attempts WHERE job_id=$id", ("$id", job.Id)));
        Assert.Equal("Canceled", Assert.Single(reopened.GetJobs(session.Id)).State);
        Assert.False(reopened.HasCompletedJob(chunk.Id, Provider));
        Assert.False(reopened.CompleteDiarizationJob(job, "{}", [], [], 0, 100));
        Assert.Empty(reopened.GetTranscriptPage(session.Id));
        Assert.Null(reopened.GetSpeakerRegistry(session.Id));
    }

    [Fact]
    public void RawAttemptValidationDoesNotSilentlyDropInvalidEvidence()
    {
        QueueChunk();
        var pending = Assert.Single(store.GetJobs(session.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveRawAttempt(pending, "{}", null));
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        Assert.Throws<ArgumentException>(() => store.SaveRawAttempt(job, " ", null));
        Assert.Throws<SqliteException>(() => store.SaveRawAttempt(job with { Id = Guid.NewGuid() }, "{}", null));
        Assert.Equal(0L, ScalarSql("SELECT count(*) FROM job_attempts"));
        Assert.Equal("Running", Assert.Single(store.GetJobs(session.Id)).State);
    }

    [Fact]
    public void RawAttemptDatabaseFailuresPropagateWithoutCompletingTheJob()
    {
        QueueChunk();
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        ExecuteSql("""
            CREATE TRIGGER reject_attempt BEFORE INSERT ON job_attempts
            BEGIN SELECT RAISE(ABORT,'synthetic evidence failure'); END;
            """);
        var error = Assert.Throws<SqliteException>(() => store.SaveRawAttempt(job, "{}", null));
        Assert.Contains("synthetic evidence failure", error.Message);
        Assert.Equal(0L, ScalarSql("SELECT count(*) FROM job_attempts"));
        Assert.Equal("Running", Assert.Single(store.GetJobs(session.Id)).State);
        Assert.Equal(job.LeaseToken, Assert.Single(store.GetJobs(session.Id)).LeaseToken);
    }

    [Fact]
    public void CloudAccountBlockingRevokesActiveLeasesAcrossSessionsButPreservesLocalWork()
    {
        store.SetConsent(session.Id, true);
        var runningChunk = QueueChunk("cloud-provider", cloud: true);
        var running = Assert.IsType<StoredJob>(store.ClaimNextJob());
        var states = new[] { "Pending", "RetryWaiting", "Paused", "Failed", "Canceled", "Succeeded", "Blocked" };
        var jobs = new Dictionary<Guid, string> { [running.Id] = "Running" };
        foreach (var state in states)
        {
            var chunk = QueueChunk("cloud-provider", cloud: true);
            var job = Assert.Single(store.GetJobs(session.Id), candidate => candidate.ChunkId == chunk.Id);
            jobs.Add(job.Id, state);
            ExecuteSql("UPDATE jobs SET state=$state,error='prior',next_attempt=9999999999999 WHERE id=$id",
                ("$state", state), ("$id", job.Id));
        }
        var localChunk = QueueChunk("local-asr");
        var (otherSession, _) = QueueOtherSessionChunk("cloud-provider", cloud: true);
        const string safeError = "Account quota exhausted. Explicit resume is required.";

        store.BlockCloudJobs(safeError);

        foreach (var job in store.GetJobs(session.Id).Where(job => jobs.ContainsKey(job.Id)))
        {
            var blocked = jobs[job.Id] is "Pending" or "RetryWaiting" or "Running";
            Assert.Equal(blocked ? "Blocked" : jobs[job.Id], job.State);
            Assert.Equal(blocked ? safeError : "prior", job.Error);
            Assert.Null(job.LeaseToken);
            if (blocked)
            {
                Assert.Equal(0L, ScalarSql("SELECT next_attempt FROM jobs WHERE id=$id", ("$id", job.Id)));
                Assert.Equal(DBNull.Value, ScalarSql("SELECT lease_until FROM jobs WHERE id=$id", ("$id", job.Id)));
            }
        }
        var other = Assert.Single(store.GetJobs(otherSession.Id));
        Assert.Equal("Blocked", other.State);
        Assert.Equal(safeError, other.Error);
        Assert.False(store.CompleteJob(running, [new(0, 100, "late", null, "Word", "Synthetic")], "{}", "test"));
        Assert.False(store.HasCompletedJob(runningChunk.Id, "cloud-provider"));
        var local = Assert.IsType<StoredJob>(store.ClaimNextJob());
        Assert.Equal(localChunk.Id, local.ChunkId);
        Assert.False(local.Cloud);
        Assert.True(store.CompleteJob(local, [], "{}", "local"));
        Assert.Null(store.ClaimNextJob());

        var reopened = new LibraryStore(root);
        reopened.RecoverInterruptedJobs();
        Assert.Null(reopened.ClaimNextJob());
        reopened.ResumeJobs(session.Id);
        var resumed = Assert.IsType<StoredJob>(reopened.ClaimNextJob());
        Assert.Equal(running.Id, resumed.Id);
        Assert.NotEqual(running.LeaseToken, resumed.LeaseToken);
        Assert.Equal("Blocked", Assert.Single(reopened.GetJobs(otherSession.Id)).State);
    }

    [Fact]
    public void CloudBlockingRequiresASafeReasonWithoutChangingQueuedJobs()
    {
        QueueChunk("cloud-provider", cloud: true);
        Assert.Throws<ArgumentException>(() => store.BlockCloudJobs(" "));
        Assert.Throws<ArgumentNullException>(() => store.BlockCloudJobs(null!));
        Assert.Equal("Pending", Assert.Single(store.GetJobs(session.Id)).State);
    }

    [Fact]
    public void ExplicitResumeRestartsCanceledJobsAfterRestartWithoutReplayingSuccess()
    {
        var chunk = QueueChunk();
        var first = Assert.IsType<StoredJob>(store.ClaimNextJob());
        var (otherSession, _) = QueueOtherSessionChunk();
        store.CancelJobs(session.Id);
        store.CancelJobs(otherSession.Id);
        var reopened = new LibraryStore(root);
        reopened.RecoverInterruptedJobs();
        reopened.ResumeProviderJobs(session.Id, Provider);
        Assert.Null(reopened.ClaimNextJob());
        Assert.Equal("Canceled", Assert.Single(reopened.GetJobs(session.Id)).State);

        reopened.ResumeJobs(session.Id);
        var second = Assert.IsType<StoredJob>(reopened.ClaimNextJob());
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Attempts);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.False(reopened.CompleteJob(first, [], "{}", "test"));
        Assert.True(reopened.CompleteJob(second, [new(0, 100, "resumed", null, "Word", "Synthetic")], "{}", "test"));
        reopened.ResumeJobs(session.Id);
        Assert.True(reopened.HasCompletedJob(chunk.Id, Provider));
        Assert.Equal("Succeeded", Assert.Single(reopened.GetJobs(session.Id)).State);
        Assert.Equal("Canceled", Assert.Single(reopened.GetJobs(otherSession.Id)).State);
        Assert.Single(reopened.GetTranscriptPage(session.Id));
        Assert.Null(reopened.ClaimNextJob());
    }

    [Fact]
    public void VersionTwoMigrationAddsAttemptsWithoutLockingAutomaticAssignments()
    {
        store.ImportCue(session.Id, track.Id, "automatic", new(0, 100, "raw", null, "Word", "Synthetic", true));
        store.ImportCue(session.Id, track.Id, "manual", new(100, 200, "raw", null, "Word", "Synthetic", true));
        store.AssignSpeaker("manual", "chosen");
        QueueChunk();
        var job = Assert.IsType<StoredJob>(store.ClaimNextJob());
        ExecuteSql("DROP TABLE job_attempts; ALTER TABLE sessions DROP COLUMN processing_state; PRAGMA user_version=2;");

        var reopened = new LibraryStore(root);
        reopened.ApplyAutomaticSpeakerAssignments([("automatic", "auto", false), ("manual", "auto", false)]);
        reopened.SaveRawAttempt(job, "{\"afterMigration\":true}", null);

        var rows = reopened.GetTranscriptPage(session.Id).ToDictionary(row => row.Id);
        Assert.Equal("auto", rows["automatic"].SpeakerId);
        Assert.Equal("chosen", rows["manual"].SpeakerId);
        Assert.Equal(4L, ScalarSql("PRAGMA user_version"));
        Assert.Equal(1L, ScalarSql("SELECT count(*) FROM job_attempts WHERE job_id=$id", ("$id", job.Id)));
        Assert.Equal("Running", Assert.Single(new LibraryStore(root).GetJobs(session.Id)).State);
    }

    [Fact]
    public void SessionDurationIsNonnegativeMonotonicAndDurable()
    {
        var duration = TimeSpan.FromHours(123).Ticks;
        store.SetSessionDuration(session.Id, duration);
        store.SetSessionDuration(session.Id, 1);
        Assert.Equal(duration, store.GetSession(session.Id).DurationTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetSessionDuration(session.Id, -1));
        var chunk = new StoredAudioChunk(Guid.NewGuid(), session.Id, track.Id, "synthetic.pcm16", 0, 16000, duration, "{}");
        store.AddNormalizedChunk(chunk);
        store.SetSessionDuration(session.Id, duration);
        Assert.Equal(duration + TimeSpan.TicksPerSecond, new LibraryStore(root).GetSession(session.Id).DurationTicks);
    }

    private StoredAudioChunk QueueChunk(string provider = Provider, bool cloud = false)
    {
        var index = chunkIndex++;
        var chunk = new StoredAudioChunk(Guid.NewGuid(), session.Id, track.Id, "synthetic.pcm16",
            index * 16000L, 16000, index * TimeSpan.TicksPerSecond, "{}");
        store.AddNormalizedChunk(chunk);
        store.QueueTranscription(chunk, provider, "en", cloud);
        return chunk;
    }

    private (StoredSession Session, StoredAudioChunk Chunk) QueueOtherSessionChunk(string provider = Provider, bool cloud = false)
    {
        var otherSession = store.CreateSession("Other synthetic session", provider, "en");
        store.SetConsent(otherSession.Id, cloud);
        var otherTrack = new StoredTrack(Guid.NewGuid(), otherSession.Id, "Import", "Synthetic", null, 0, null);
        store.AddTrack(otherTrack);
        var chunk = new StoredAudioChunk(Guid.NewGuid(), otherSession.Id, otherTrack.Id, "synthetic.pcm16", 0, 16000, 0, "{}");
        store.AddNormalizedChunk(chunk);
        store.QueueTranscription(chunk, provider, "en", cloud);
        return (otherSession, chunk);
    }

    private void ExecuteSql(string sql, params (string Name, object Value)[] values) =>
        WithSql(sql, command => command.ExecuteNonQuery(), values);

    private object? ScalarSql(string sql, params (string Name, object Value)[] values) =>
        WithSql(sql, command => command.ExecuteScalar(), values);

    private object? WithSql(string sql, Func<SqliteCommand, object?> execute, params (string Name, object Value)[] values)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "library.sqlite3"), ForeignKeys = true, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value);
        return execute(command);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
