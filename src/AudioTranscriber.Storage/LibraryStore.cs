using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AudioTranscriber.Storage;

public sealed class LibraryStore
{
    private const int CurrentSchemaVersion = 4;
    private readonly string connectionString;
    private readonly object gate = new();
    public string RootDirectory { get; }

    public LibraryStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(RootDirectory, "library.sqlite3"),
            ForeignKeys = true,
            DefaultTimeout = 15
        }.ToString();
        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        using var transaction = connection.BeginTransaction();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var schemaVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException("This library was created by a newer AudioTranscriber version.");
        Execute(connection, Schema);
        if (schemaVersion < 2)
        {
            // Version 1 did not distinguish automatic assignments from manual choices, including explicit Unknown.
            Execute(connection, """
                ALTER TABLE segments ADD COLUMN manual_speaker INTEGER NOT NULL DEFAULT 0 CHECK(manual_speaker IN (0,1));
                UPDATE segments SET manual_speaker=1;
                PRAGMA user_version=2;
                """);
        }
        if (schemaVersion < 3)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS job_attempts(
                    job_id TEXT NOT NULL REFERENCES jobs(id),attempt INTEGER NOT NULL CHECK(attempt>0),
                    raw_json TEXT NOT NULL,model TEXT,PRIMARY KEY(job_id,attempt));
                PRAGMA user_version=3;
                """);
        }
        if (schemaVersion < 4)
        {
            Execute(connection, """
                ALTER TABLE sessions ADD COLUMN processing_state TEXT NOT NULL DEFAULT 'Running'
                    CHECK(processing_state IN ('Running','Paused','Canceled'));
                UPDATE sessions SET processing_state='Canceled'
                    WHERE EXISTS(SELECT 1 FROM jobs WHERE session_id=sessions.id AND state='Canceled');
                UPDATE sessions SET processing_state='Paused'
                    WHERE EXISTS(SELECT 1 FROM jobs WHERE session_id=sessions.id AND state='Paused');
                PRAGMA user_version=4;
                """);
        }
        transaction.Commit();
    }

    public void CloseConnections()
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public StoredSession CreateSession(string name, string providerId, string language)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("A session name and source language are required.");
        var id = Guid.NewGuid();
        var directory = Path.Combine(RootDirectory, "sessions", id.ToString("N"));
        Directory.CreateDirectory(directory);
        var created = DateTimeOffset.UtcNow;
        Write("INSERT INTO sessions(id,name,directory,created,state,consent,provider,language,duration) VALUES($id,$name,$dir,$created,'Created',0,$provider,$language,0)",
            ("$id", id), ("$name", name.Trim()), ("$dir", directory), ("$created", created), ("$provider", providerId), ("$language", language));
        return GetSession(id);
    }

    public StoredSession GetSession(Guid id) =>
        Read("SELECT * FROM sessions WHERE id=$id", ReadSession, ("$id", id)).Single();

    public IReadOnlyList<StoredSession> GetSessions(int limit = 200) =>
        Read("SELECT * FROM sessions ORDER BY created DESC LIMIT $limit", ReadSession, ("$limit", Math.Clamp(limit, 1, 2000)));

    public void SetSessionState(Guid sessionId, string state, string? error = null) =>
        Write("UPDATE sessions SET state=$state,error=$error WHERE id=$id", ("$state", state), ("$error", error), ("$id", sessionId));

    public void SetSessionDuration(Guid sessionId, long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        Write("UPDATE sessions SET duration=max(duration,$ticks) WHERE id=$id", ("$ticks", ticks), ("$id", sessionId));
    }

    public void SetConsent(Guid sessionId, bool permitted) =>
        Write("UPDATE sessions SET consent=$consent WHERE id=$id", ("$consent", permitted), ("$id", sessionId));

    public void SetProvider(Guid sessionId, string providerId, string language) =>
        Write("UPDATE sessions SET provider=$provider,language=$language WHERE id=$id",
            ("$id", sessionId), ("$provider", providerId), ("$language", language));

    public void AddTrack(StoredTrack track) =>
        Write("INSERT INTO tracks(id,session_id,kind,name,original_path,stream_index,metadata) VALUES($id,$session,$kind,$name,$path,$stream,$metadata)",
            ("$id", track.Id), ("$session", track.SessionId), ("$kind", track.Kind), ("$name", track.Name),
            ("$path", track.OriginalPath), ("$stream", track.AudioStreamIndex), ("$metadata", track.MetadataJson));

    public void UpdateTrackSource(Guid trackId, string originalPath, int streamIndex, string metadata) =>
        Write("UPDATE tracks SET original_path=$path,stream_index=$stream,metadata=$metadata WHERE id=$id",
            ("$id", trackId), ("$path", originalPath), ("$stream", streamIndex), ("$metadata", metadata));

    public IReadOnlyList<StoredTrack> GetTracks(Guid sessionId) =>
        Read("SELECT * FROM tracks WHERE session_id=$session ORDER BY rowid",
            r => new StoredTrack(G(r, "id"), G(r, "session_id"), S(r, "kind"), S(r, "name"), N(r, "original_path"),
                (int)L(r, "stream_index"), N(r, "metadata")), ("$session", sessionId));

    public void AddArchiveChunk(Guid chunkId, Guid trackId, string path, long sourceFrame, long frames, long startTicks, string metadata) =>
        Write("INSERT OR IGNORE INTO archive_chunks(id,track_id,path,source_frame,frames,start_ticks,metadata) VALUES($id,$track,$path,$source,$frames,$ticks,$metadata)",
            ("$id", chunkId), ("$track", trackId), ("$path", path), ("$source", sourceFrame), ("$frames", frames),
            ("$ticks", startTicks), ("$metadata", metadata));

    public IReadOnlyList<string> GetArchiveManifests(Guid trackId) =>
        Read("SELECT metadata FROM archive_chunks WHERE track_id=$track ORDER BY source_frame", r => r.GetString(0), ("$track", trackId));

    public void AddNormalizedChunk(StoredAudioChunk chunk)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, """
                INSERT OR IGNORE INTO normalized_chunks(id,session_id,track_id,path,start_sample,sample_count,start_ticks,metadata)
                VALUES($id,$session,$track,$path,$start,$count,$ticks,$metadata)
                """, ("$id", chunk.Id), ("$session", chunk.SessionId), ("$track", chunk.TrackId), ("$path", chunk.Path),
                ("$start", chunk.StartSample), ("$count", chunk.SampleCount), ("$ticks", chunk.StartTicks), ("$metadata", chunk.MetadataJson));
            Execute(connection, "UPDATE sessions SET duration=max(duration,$end) WHERE id=$id",
                ("$end", checked(chunk.StartTicks + chunk.SampleCount * TimeSpan.TicksPerSecond / 16000L)), ("$id", chunk.SessionId));
            transaction.Commit();
        }
    }

    public IReadOnlyList<StoredAudioChunk> GetChunks(Guid trackId) =>
        Read("SELECT * FROM normalized_chunks WHERE track_id=$track ORDER BY start_sample", ReadChunk, ("$track", trackId));

    public StoredAudioChunk GetChunk(Guid id) =>
        Read("SELECT * FROM normalized_chunks WHERE id=$id", ReadChunk, ("$id", id)).Single();

    public void QueueTranscription(StoredAudioChunk chunk, string providerId, string language, bool cloud) =>
        Write("""
            INSERT OR IGNORE INTO jobs(id,session_id,track_id,chunk_id,provider,language,cloud,state,attempts,next_attempt)
            VALUES($id,$session,$track,$chunk,$provider,$language,$cloud,
                CASE (SELECT processing_state FROM sessions WHERE id=$session)
                    WHEN 'Paused' THEN 'Paused' WHEN 'Canceled' THEN 'Canceled' ELSE 'Pending' END,0,0)
            """, ("$id", Guid.NewGuid()), ("$session", chunk.SessionId), ("$track", chunk.TrackId), ("$chunk", chunk.Id),
            ("$provider", providerId), ("$language", language), ("$cloud", cloud));

    public bool HasCompletedJob(Guid chunkId, string providerId) =>
        Read("SELECT EXISTS(SELECT 1 FROM jobs WHERE chunk_id=$chunk AND provider=$provider AND state='Succeeded')",
            r => r.GetInt64(0) != 0, ("$chunk", chunkId), ("$provider", providerId)).Single();

    public StoredJob? ClaimNextJob()
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Execute(connection, "UPDATE jobs SET state='Pending',lease=NULL,lease_until=NULL WHERE state='Running' AND lease_until<$now", ("$now", now));
            using var command = Command(connection, """
                SELECT j.* FROM jobs j JOIN sessions s ON s.id=j.session_id
                WHERE j.state IN ('Pending','RetryWaiting') AND j.next_attempt <= $now
                AND s.processing_state='Running'
                AND (j.cloud=0 OR s.consent=1)
                ORDER BY j.rowid LIMIT 1
                """, ("$now", now));
            StoredJob? job;
            using (var reader = command.ExecuteReader()) job = reader.Read() ? ReadJob(reader) : null;
            if (job is null) return null;
            var lease = Guid.NewGuid().ToString("N");
            Execute(connection, "UPDATE jobs SET state='Running',attempts=attempts+1,lease=$lease,lease_until=$until,error=NULL WHERE id=$id",
                ("$lease", lease), ("$until", now + 5 * 60_000), ("$id", job.Id));
            transaction.Commit();
            return job with { State = "Running", Attempts = job.Attempts + 1, LeaseToken = lease, Error = null };
        }
    }

    public void RenewLease(StoredJob job) =>
        Write("UPDATE jobs SET lease_until=$until WHERE id=$id AND state='Running' AND lease=$lease",
            ("$until", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()), ("$id", job.Id), ("$lease", job.LeaseToken));

    public bool TryRenewLease(StoredJob job)
    {
        lock (gate)
        {
            using var connection = Open();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Execute(connection, """
                UPDATE jobs SET lease_until=$until WHERE id=$id AND state='Running'
                AND lease=$lease AND lease_until>$now
                """, ("$until", now + 5 * 60_000), ("$now", now), ("$id", job.Id), ("$lease", job.LeaseToken)) == 1;
        }
    }

    public void SaveRawAttempt(StoredJob job, string rawJson, string? observedModel)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(job.Attempts);
        // Evidence is independent of the live lease: retaining a late response must not resurrect its job.
        Write("""
            INSERT INTO job_attempts(job_id,attempt,raw_json,model) VALUES($job,$attempt,$raw,$model)
            ON CONFLICT(job_id,attempt) DO NOTHING
            """, ("$job", job.Id), ("$attempt", job.Attempts), ("$raw", rawJson), ("$model", observedModel));
    }

    public bool CompleteJob(StoredJob job, IReadOnlyList<SegmentDraft> rows, string rawJson, string actualModel)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (Execute(connection, "UPDATE jobs SET state='Succeeded',lease=NULL,lease_until=NULL,error=NULL WHERE id=$id AND state='Running' AND lease=$lease",
                    ("$id", job.Id), ("$lease", job.LeaseToken)) != 1) return false;
            Execute(connection, "INSERT INTO results(job_id,model,raw_json) VALUES($id,$model,$raw)",
                ("$id", job.Id), ("$model", actualModel), ("$raw", rawJson));
            for (var index = 0; index < rows.Count; index++)
                InsertSegment(connection, $"{job.Id:N}-{index:D6}", job.SessionId, job.TrackId, job.Id, rows[index]);
            transaction.Commit();
            return true;
        }
    }

    public bool CompleteDiarizationJob(StoredJob job, string registryJson, IReadOnlyList<StoredSpeaker> speakers,
        IReadOnlyList<StoredTurn> turns, long startTicks, long endTicks)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(registryJson);
        ArgumentNullException.ThrowIfNull(speakers);
        ArgumentNullException.ThrowIfNull(turns);
        if (startTicks < 0 || endTicks < startTicks)
            throw new InvalidDataException("Diarization timing must be nonnegative and ordered.");
        if (speakers.Any(speaker => speaker.SessionId != job.SessionId))
            throw new InvalidDataException("Diarization speakers must belong to the job's session.");
        if (turns.Any(turn => turn.TrackId != job.TrackId || turn.StartTicks < startTicks || turn.StartTicks >= endTicks
                || turn.EndTicks < turn.StartTicks || turn.EndTicks > endTicks))
            throw new InvalidDataException("Diarization turns must belong to the job's track and replacement interval.");

        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (Execute(connection, """
                UPDATE jobs SET state='Succeeded',lease=NULL,lease_until=NULL,error=NULL
                WHERE id=$id AND session_id=$session AND track_id=$track AND chunk_id=$chunk
                AND provider=$provider AND language=$language AND state='Running' AND lease=$lease AND lease_until>$now
                """, ("$id", job.Id), ("$session", job.SessionId), ("$track", job.TrackId), ("$chunk", job.ChunkId),
                ("$provider", job.ProviderId), ("$language", job.Language), ("$lease", job.LeaseToken),
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) != 1) return false;
            Execute(connection, "UPDATE sessions SET registry=$registry WHERE id=$session",
                ("$registry", registryJson), ("$session", job.SessionId));
            foreach (var speaker in speakers) UpsertSpeaker(connection, speaker);
            ReplaceTurns(connection, job.TrackId, startTicks, endTicks, turns);
            transaction.Commit();
            return true;
        }
    }

    public void FailJob(StoredJob job, string safeError, string state = "Failed", TimeSpan? retryAfter = null)
    {
        if (state is not ("Failed" or "Blocked" or "RetryWaiting" or "Pending" or "Paused" or "Canceled"))
            throw new ArgumentException("Invalid job failure state.", nameof(state));
        Write("UPDATE jobs SET state=$state,error=$error,lease=NULL,lease_until=NULL,next_attempt=$next WHERE id=$id AND state='Running' AND lease=$lease",
            ("$state", state), ("$error", safeError), ("$next", DateTimeOffset.UtcNow.Add(retryAfter ?? TimeSpan.Zero).ToUnixTimeMilliseconds()),
            ("$id", job.Id), ("$lease", job.LeaseToken));
    }

    /// <summary>Returns a claimed job to the queue without counting the claim as an attempt.</summary>
    public void DeferJob(StoredJob job, string reason, TimeSpan delay) =>
        Write("""
            UPDATE jobs SET state='Pending',error=$error,attempts=MAX(0,attempts-1),lease=NULL,lease_until=NULL,next_attempt=$next
            WHERE id=$id AND state='Running' AND lease=$lease
            """, ("$error", reason), ("$next", DateTimeOffset.UtcNow.Add(delay).ToUnixTimeMilliseconds()),
            ("$id", job.Id), ("$lease", job.LeaseToken));

    /// <summary>Makes blocked or deferred jobs for a provider runnable now, across all sessions.</summary>
    public void ReleaseProviderJobs(string providerId) =>
        Write("""
            UPDATE jobs SET state='Pending',next_attempt=0,error=NULL,lease=NULL,lease_until=NULL
            WHERE provider=$provider AND state IN ('Blocked','Pending','RetryWaiting')
            """, ("$provider", providerId));

    public void PauseJobs(Guid sessionId) =>
        SetProcessingState(sessionId, "Paused",
            "UPDATE jobs SET state='Paused',lease=NULL,lease_until=NULL WHERE session_id=$session AND state IN ('Pending','RetryWaiting','Running')");

    public void BlockCloudJobs(string safeError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeError);
        Write("""
            UPDATE jobs SET state='Blocked',error=$error,next_attempt=0,lease=NULL,lease_until=NULL
            WHERE cloud=1 AND state IN ('Pending','RetryWaiting','Running')
            """, ("$error", safeError));
    }

    public void ResumeJobs(Guid sessionId) =>
        SetProcessingState(sessionId, "Running", """
            UPDATE jobs SET state='Pending',next_attempt=0,error=NULL,lease=NULL,lease_until=NULL
            WHERE session_id=$session AND state IN ('Paused','Blocked','Failed','Canceled')
            """);

    public void ResumeProviderJobs(Guid sessionId, string providerId) =>
        Write("""
            UPDATE jobs SET state='Pending',next_attempt=0,error=NULL,lease=NULL,lease_until=NULL
            WHERE session_id=$session AND provider=$provider AND state IN ('Paused','Blocked','Failed')
            """, ("$session", sessionId), ("$provider", providerId));

    public void CancelJobs(Guid sessionId) =>
        SetProcessingState(sessionId, "Canceled",
            "UPDATE jobs SET state='Canceled',lease=NULL,lease_until=NULL WHERE session_id=$session AND state IN ('Pending','RetryWaiting','Running','Paused','Blocked')");

    private void SetProcessingState(Guid sessionId, string state, string jobUpdate)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (Execute(connection, "UPDATE sessions SET processing_state=$state WHERE id=$session",
                    ("$state", state), ("$session", sessionId)) != 1)
                throw new InvalidOperationException("The session does not exist.");
            Execute(connection, jobUpdate, ("$session", sessionId));
            transaction.Commit();
        }
    }

    public void RecoverInterruptedJobs() =>
        Write("""
            UPDATE jobs SET state='Pending',lease=NULL,lease_until=NULL WHERE state='Running';
            UPDATE sessions SET state='Recoverable',error='Recording was interrupted. Original chunks are retained; review recovery before starting a new capture.'
            WHERE state IN ('Starting','Recording','Stopping','Importing');
            """);

    public QueueProgress GetProgress(Guid sessionId)
    {
        var counts = Read("SELECT state,count(*) AS count FROM jobs WHERE session_id=$session GROUP BY state",
            r => (State: S(r, "state"), Count: (int)L(r, "count")), ("$session", sessionId))
            .ToDictionary(x => x.State, x => x.Count);
        int Count(params string[] states) => states.Sum(s => counts.GetValueOrDefault(s));
        return new(Count("Pending", "RetryWaiting"), Count("Running"), Count("Succeeded"),
            Count("Failed", "Blocked"), Count("Paused", "Canceled"));
    }

    public IReadOnlyList<StoredJob> GetJobs(Guid sessionId, int limit = 100) =>
        Read("SELECT * FROM jobs WHERE session_id=$session ORDER BY rowid DESC LIMIT $limit", ReadJob,
            ("$session", sessionId), ("$limit", Math.Clamp(limit, 1, 1000)));

    public IReadOnlyList<TranscriptRow> GetTranscriptPage(
        Guid sessionId, string? search = null, string? speakerId = null, TranscriptCursor? after = null,
        int limit = 200, long? seekTicks = null)
    {
        var values = new List<(string, object?)>
        {
            ("$session", sessionId), ("$limit", Math.Clamp(limit, 1, 1000))
        };
        var sql = TranscriptSelect + " WHERE t.session_id=$session";
        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += " AND t.rowid IN (SELECT rowid FROM transcript_fts WHERE transcript_fts MATCH $search)";
            values.Add(("$search", string.Join(" AND ", search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""))));
        }
        if (speakerId is not null)
        {
            sql += speakerId.Length == 0 ? " AND t.speaker_id IS NULL" : " AND t.speaker_id=$speaker";
            if (speakerId.Length > 0) values.Add(("$speaker", speakerId));
        }
        if (after is not null)
        {
            sql += " AND (t.start_ticks>$start OR (t.start_ticks=$start AND t.id>$after))";
            values.Add(("$start", after.StartTicks));
            values.Add(("$after", after.Id));
        }
        if (seekTicks is not null)
        {
            sql += " AND t.end_ticks >= $seek";
            values.Add(("$seek", seekTicks.Value));
        }
        sql += " ORDER BY t.start_ticks,t.id LIMIT $limit";
        return Read(sql, ReadTranscript, values.ToArray());
    }

    public IEnumerable<TranscriptRow> EnumerateTranscript(Guid sessionId)
    {
        TranscriptCursor? cursor = null;
        while (true)
        {
            var page = GetTranscriptPage(sessionId, after: cursor, limit: 500);
            foreach (var row in page) yield return row;
            if (page.Count < 500) yield break;
            var last = page[^1];
            cursor = new(last.StartTicks, last.Id);
        }
    }

    /// <summary>Transcript rows in insertion order after a sequence number, for following a session live.</summary>
    public IReadOnlyList<(long Sequence, TranscriptRow Row)> GetSegmentsAddedAfter(Guid sessionId, long afterSequence, int limit = 100) =>
        Read(TranscriptSelect.Replace("SELECT t.*", "SELECT t.rowid AS seq,t.*", StringComparison.Ordinal) +
            " WHERE t.session_id=$session AND t.rowid>$after ORDER BY t.rowid LIMIT $limit",
            r => (L(r, "seq"), ReadTranscript(r)), ("$session", sessionId), ("$after", afterSequence), ("$limit", Math.Clamp(limit, 1, 1000)));

    public long GetLatestSegmentSequence(Guid sessionId) =>
        Read("SELECT coalesce(max(rowid),0) FROM segments WHERE session_id=$session", r => r.GetInt64(0), ("$session", sessionId)).Single();

    public int CountSegments(Guid sessionId) =>
        Read("SELECT count(*) FROM segments WHERE session_id=$session", r => (int)r.GetInt64(0), ("$session", sessionId)).Single();

    public int CountJobSegments(Guid jobId) =>
        Read("SELECT count(*) FROM segments WHERE job_id=$job", r => (int)r.GetInt64(0), ("$job", jobId)).Single();

    public void CorrectSegment(string id, string? correction) =>
        Write("UPDATE segments SET correction=$text WHERE id=$id", ("$text", correction), ("$id", id));

    public void AssignSpeaker(string id, string? speakerId) =>
        Write("UPDATE segments SET speaker_id=$speaker,uncertain=0,manual_speaker=1 WHERE id=$id", ("$speaker", speakerId), ("$id", id));

    public void ApplyAutomaticSpeakerAssignments(IReadOnlyList<(string SegmentId, string? SpeakerId, bool Uncertain)> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var assignment in assignments)
                Execute(connection, """
                    UPDATE segments SET speaker_id=$speaker,uncertain=$uncertain WHERE id=$id AND manual_speaker=0
                    """, ("$speaker", assignment.SpeakerId), ("$uncertain", assignment.Uncertain), ("$id", assignment.SegmentId));
            transaction.Commit();
        }
    }

    public void ImportCue(Guid sessionId, Guid trackId, string sourceKey, SegmentDraft cue) =>
        WithWrite(connection => InsertSegment(connection, sourceKey, sessionId, trackId, null, cue));

    public void UpsertSpeaker(StoredSpeaker speaker) =>
        WithWrite(connection => UpsertSpeaker(connection, speaker));

    public IReadOnlyList<StoredSpeaker> GetSpeakers(Guid sessionId) =>
        Read("SELECT * FROM speakers WHERE session_id=$session ORDER BY rowid",
            r => new StoredSpeaker(S(r, "id"), G(r, "session_id"), S(r, "name"), N(r, "participant_id"), S(r, "provenance")),
            ("$session", sessionId));

    public void RenameSpeaker(Guid sessionId, string speakerId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A speaker name is required.");
        Write("UPDATE speakers SET name=$name WHERE session_id=$session AND id=$id",
            ("$name", name.Trim()), ("$session", sessionId), ("$id", speakerId));
    }

    public string? GetSpeakerRegistry(Guid sessionId) =>
        Read("SELECT registry FROM sessions WHERE id=$session", r => r.IsDBNull(0) ? null : r.GetString(0), ("$session", sessionId)).Single();

    public void SetSpeakerRegistry(Guid sessionId, string json) =>
        Write("UPDATE sessions SET registry=$registry WHERE id=$session", ("$registry", json), ("$session", sessionId));

    public void ReplaceTurns(Guid trackId, long startTicks, long endTicks, IEnumerable<StoredTurn> turns)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            ReplaceTurns(connection, trackId, startTicks, endTicks, turns);
            transaction.Commit();
        }
    }

    public IReadOnlyList<StoredTurn> GetTurns(Guid trackId, long startTicks, long endTicks) =>
        Read("SELECT * FROM turns WHERE track_id=$track AND start_ticks < $end AND end_ticks > $start ORDER BY start_ticks",
            r => new StoredTurn(G(r, "track_id"), L(r, "start_ticks"), L(r, "end_ticks"), N(r, "speaker_id"),
                L(r, "overlap") != 0, L(r, "uncertain") != 0), ("$track", trackId), ("$start", startTicks), ("$end", endTicks));

    private static void UpsertSpeaker(SqliteConnection connection, StoredSpeaker speaker) =>
        Execute(connection, """
            INSERT INTO speakers(id,session_id,name,participant_id,provenance)
            VALUES($id,$session,$name,$participant,$provenance)
            ON CONFLICT(session_id,id) DO UPDATE SET participant_id=coalesce(excluded.participant_id,speakers.participant_id)
            """, ("$id", speaker.Id), ("$session", speaker.SessionId), ("$name", speaker.Name),
            ("$participant", speaker.ParticipantId), ("$provenance", speaker.Provenance));

    private static void ReplaceTurns(SqliteConnection connection, Guid trackId, long startTicks, long endTicks, IEnumerable<StoredTurn> turns)
    {
        Execute(connection, "DELETE FROM turns WHERE track_id=$track AND start_ticks >= $start AND start_ticks < $end",
            ("$track", trackId), ("$start", startTicks), ("$end", endTicks));
        foreach (var turn in turns)
            Execute(connection, "INSERT INTO turns(track_id,start_ticks,end_ticks,speaker_id,overlap,uncertain) VALUES($track,$start,$end,$speaker,$overlap,$uncertain)",
                ("$track", trackId), ("$start", turn.StartTicks), ("$end", turn.EndTicks), ("$speaker", turn.SpeakerId),
                ("$overlap", turn.Overlap), ("$uncertain", turn.Uncertain));
    }

    private static void InsertSegment(SqliteConnection connection, string id, Guid sessionId, Guid trackId, Guid? jobId, SegmentDraft row)
    {
        if (row.StartTicks < 0 || row.EndTicks < row.StartTicks)
            throw new InvalidDataException("Transcript timing must be nonnegative and ordered.");
        Execute(connection, """
            INSERT OR IGNORE INTO segments(id,session_id,track_id,job_id,start_ticks,end_ticks,raw_text,speaker_id,granularity,provenance,uncertain)
            VALUES($id,$session,$track,$job,$start,$end,$text,$speaker,$granularity,$provenance,$uncertain)
            """, ("$id", id), ("$session", sessionId), ("$track", trackId), ("$job", jobId),
            ("$start", row.StartTicks), ("$end", row.EndTicks), ("$text", row.Text), ("$speaker", row.SpeakerId),
            ("$granularity", row.TimingGranularity), ("$provenance", row.Provenance), ("$uncertain", row.Uncertain));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private void Write(string sql, params (string, object?)[] values) =>
        WithWrite(connection => Execute(connection, sql, values));

    private void WithWrite(Action<SqliteConnection> action)
    {
        lock (gate) { using var connection = Open(); action(connection); }
    }

    private List<T> Read<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] values)
    {
        using var connection = Open();
        using var command = Command(connection, sql, values);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                Guid guid => guid.ToString("D"),
                DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
                bool flag => flag ? 1 : 0,
                _ => value
            });
        return command;
    }

    private static int Execute(SqliteConnection connection, string sql, params (string, object?)[] values)
    {
        using var command = Command(connection, sql, values);
        return command.ExecuteNonQuery();
    }

    private static string S(SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));
    private static string? N(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : S(reader, name);
    private static long L(SqliteDataReader reader, string name) => reader.GetInt64(reader.GetOrdinal(name));
    private static Guid G(SqliteDataReader reader, string name) => Guid.Parse(S(reader, name));
    private static StoredSession ReadSession(SqliteDataReader r) =>
        new(G(r, "id"), S(r, "name"), S(r, "directory"), DateTimeOffset.Parse(S(r, "created"), CultureInfo.InvariantCulture),
            S(r, "state"), L(r, "consent") != 0, S(r, "provider"), S(r, "language"), N(r, "error"), L(r, "duration"),
            S(r, "processing_state"));
    private static StoredAudioChunk ReadChunk(SqliteDataReader r) =>
        new(G(r, "id"), G(r, "session_id"), G(r, "track_id"), S(r, "path"), L(r, "start_sample"),
            (int)L(r, "sample_count"), L(r, "start_ticks"), S(r, "metadata"));
    private static StoredJob ReadJob(SqliteDataReader r) =>
        new(G(r, "id"), G(r, "session_id"), G(r, "track_id"), G(r, "chunk_id"), S(r, "provider"), S(r, "language"),
            L(r, "cloud") != 0, S(r, "state"), (int)L(r, "attempts"), N(r, "lease"), N(r, "error"));
    private static TranscriptRow ReadTranscript(SqliteDataReader r) =>
        new(S(r, "id"), G(r, "session_id"), G(r, "track_id"), L(r, "start_ticks"), L(r, "end_ticks"),
            S(r, "raw_text"), N(r, "correction"), N(r, "speaker_id"), S(r, "speaker_name"), S(r, "granularity"),
            S(r, "provenance"), L(r, "uncertain") != 0);

    private const string TranscriptSelect = """
        SELECT t.*,coalesce(s.name,CASE WHEN t.speaker_id IS NOT NULL THEN t.speaker_id WHEN (SELECT kind FROM tracks WHERE id=t.track_id)='Microphone' THEN 'Me (mic)' ELSE 'Unknown' END) AS speaker_name
        FROM segments t LEFT JOIN speakers s ON s.id=t.speaker_id AND s.session_id=t.session_id
        """;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS sessions(
            id TEXT PRIMARY KEY,name TEXT NOT NULL,directory TEXT NOT NULL,created TEXT NOT NULL,state TEXT NOT NULL,
            consent INTEGER NOT NULL DEFAULT 0,provider TEXT NOT NULL,language TEXT NOT NULL,error TEXT,
            duration INTEGER NOT NULL DEFAULT 0,registry TEXT);
        CREATE TABLE IF NOT EXISTS tracks(
            id TEXT PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id),kind TEXT NOT NULL,name TEXT NOT NULL,
            original_path TEXT,stream_index INTEGER NOT NULL DEFAULT 0,metadata TEXT);
        CREATE TABLE IF NOT EXISTS archive_chunks(
            id TEXT PRIMARY KEY,track_id TEXT NOT NULL REFERENCES tracks(id),path TEXT NOT NULL,
            source_frame INTEGER NOT NULL,frames INTEGER NOT NULL,start_ticks INTEGER NOT NULL,metadata TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS archive_track ON archive_chunks(track_id,source_frame);
        CREATE TABLE IF NOT EXISTS normalized_chunks(
            id TEXT PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id),track_id TEXT NOT NULL REFERENCES tracks(id),
            path TEXT NOT NULL,start_sample INTEGER NOT NULL,sample_count INTEGER NOT NULL,start_ticks INTEGER NOT NULL,metadata TEXT NOT NULL,
            UNIQUE(track_id,start_sample));
        CREATE TABLE IF NOT EXISTS jobs(
            id TEXT PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id),track_id TEXT NOT NULL REFERENCES tracks(id),
            chunk_id TEXT NOT NULL REFERENCES normalized_chunks(id),provider TEXT NOT NULL,language TEXT NOT NULL,
            cloud INTEGER NOT NULL,state TEXT NOT NULL,attempts INTEGER NOT NULL,next_attempt INTEGER NOT NULL,
            lease TEXT,lease_until INTEGER,error TEXT,UNIQUE(chunk_id,provider,language));
        CREATE INDEX IF NOT EXISTS jobs_ready ON jobs(state,next_attempt);
        CREATE TABLE IF NOT EXISTS results(job_id TEXT PRIMARY KEY REFERENCES jobs(id),model TEXT NOT NULL,raw_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS speakers(
            id TEXT NOT NULL,session_id TEXT NOT NULL REFERENCES sessions(id),name TEXT NOT NULL,participant_id TEXT,
            provenance TEXT NOT NULL,PRIMARY KEY(session_id,id));
        CREATE TABLE IF NOT EXISTS segments(
            id TEXT PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id),track_id TEXT NOT NULL REFERENCES tracks(id),
            job_id TEXT REFERENCES jobs(id),start_ticks INTEGER NOT NULL,end_ticks INTEGER NOT NULL,raw_text TEXT NOT NULL,
            correction TEXT,speaker_id TEXT,granularity TEXT NOT NULL,provenance TEXT NOT NULL,uncertain INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS segment_page ON segments(session_id,start_ticks,id);
        CREATE INDEX IF NOT EXISTS segment_speaker ON segments(session_id,speaker_id,start_ticks,id);
        CREATE VIRTUAL TABLE IF NOT EXISTS transcript_fts USING fts5(text);
        CREATE TRIGGER IF NOT EXISTS segment_insert AFTER INSERT ON segments BEGIN
            INSERT INTO transcript_fts(rowid,text) VALUES(new.rowid,coalesce(new.correction,new.raw_text)); END;
        CREATE TRIGGER IF NOT EXISTS segment_update AFTER UPDATE OF correction,raw_text ON segments BEGIN
            UPDATE transcript_fts SET text=coalesce(new.correction,new.raw_text) WHERE rowid=new.rowid; END;
        CREATE TRIGGER IF NOT EXISTS segment_delete AFTER DELETE ON segments BEGIN
            DELETE FROM transcript_fts WHERE rowid=old.rowid; END;
        CREATE TABLE IF NOT EXISTS turns(
            track_id TEXT NOT NULL REFERENCES tracks(id),start_ticks INTEGER NOT NULL,end_ticks INTEGER NOT NULL,
            speaker_id TEXT,overlap INTEGER NOT NULL,uncertain INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS turn_time ON turns(track_id,start_ticks,end_ticks);
        """;
}
