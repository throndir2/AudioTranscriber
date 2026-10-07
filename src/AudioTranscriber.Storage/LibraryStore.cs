using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AudioTranscriber.Storage;

public sealed class LibraryStore
{
    private const int CurrentSchemaVersion = 6;
    // Shown for microphone lines that have no speaker yet.
    public const string MicrophoneDefaultName = "Me (mic)";
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
        if (schemaVersion < 5)
        {
            // Speakers matched to the user's labeled lines by voice; background speaker analysis leaves them alone.
            Execute(connection, """
                ALTER TABLE segments ADD COLUMN voice_fill INTEGER NOT NULL DEFAULT 0 CHECK(voice_fill IN (0,1));
                PRAGMA user_version=5;
                """);
        }
        if (schemaVersion < 6)
        {
            // The speaker every microphone line of the session is labeled with, including lines transcribed later.
            Execute(connection, """
                ALTER TABLE sessions ADD COLUMN mic_speaker TEXT;
                PRAGMA user_version=6;
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

    /// <summary>Removes a session and every row that belongs to it. Returns its folder, or null if it did not exist.</summary>
    public string? DeleteSession(Guid sessionId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            string? directory;
            using (var find = Command(connection, "SELECT directory FROM sessions WHERE id=$session", ("$session", sessionId)))
                directory = find.ExecuteScalar() as string;
            if (directory is null) return null;
            const string jobs = "SELECT id FROM jobs WHERE session_id=$session";
            const string tracks = "SELECT id FROM tracks WHERE session_id=$session";
            foreach (var sql in new[]
                     {
                         $"DELETE FROM job_attempts WHERE job_id IN ({jobs})",
                         $"DELETE FROM results WHERE job_id IN ({jobs})",
                         "DELETE FROM segments WHERE session_id=$session",
                         $"DELETE FROM turns WHERE track_id IN ({tracks})",
                         $"DELETE FROM speaker_hints WHERE track_id IN ({tracks})",
                         "DELETE FROM speakers WHERE session_id=$session",
                         "DELETE FROM jobs WHERE session_id=$session",
                         "DELETE FROM normalized_chunks WHERE session_id=$session",
                         $"DELETE FROM archive_chunks WHERE track_id IN ({tracks})",
                         "DELETE FROM tracks WHERE session_id=$session",
                         "DELETE FROM merged_folders WHERE session_id=$session",
                         "DELETE FROM sessions WHERE id=$session"
                     })
                Execute(connection, sql, ("$session", sessionId));
            transaction.Commit();
            return directory;
        }
    }

    /// <summary>Folders of sessions that were merged into this one; their audio files stay where they were recorded.</summary>
    public IReadOnlyList<string> GetMergedFolders(Guid sessionId) =>
        Read("SELECT directory FROM merged_folders WHERE session_id=$session", r => r.GetString(0), ("$session", sessionId));

    /// <summary>Stops the scheduler from claiming this session's jobs without changing their states.</summary>
    public void SetProcessingStateOnly(Guid sessionId, string state) =>
        Write("UPDATE sessions SET processing_state=$state WHERE id=$id", ("$state", state), ("$id", sessionId));

    /// <summary>
    /// Moves everything of the source session into the target, shifted by OffsetTicks on the target's timeline,
    /// then removes the source session row. Files are not moved; the source folder is remembered for deletion.
    /// </summary>
    public void MergeSessions(SessionMerge merge)
    {
        ArgumentNullException.ThrowIfNull(merge);
        if (merge.SourceId == merge.TargetId) throw new ArgumentException("A session cannot be merged into itself.");
        ArgumentOutOfRangeException.ThrowIfNegative(merge.OffsetTicks);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            string? directory;
            long duration;
            using (var find = Command(connection, "SELECT directory,duration FROM sessions WHERE id=$source", ("$source", merge.SourceId)))
            using (var reader = find.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("The session to merge no longer exists.");
                directory = reader.GetString(0);
                duration = reader.GetInt64(1);
            }
            if (Execute(connection, "UPDATE sessions SET duration=max(duration,$end) WHERE id=$target",
                    ("$end", checked(merge.OffsetTicks + duration)), ("$target", merge.TargetId)) != 1)
                throw new InvalidOperationException("The session to merge into no longer exists.");
            (string, object?) source = ("$source", merge.SourceId), target = ("$target", merge.TargetId), offset = ("$offset", merge.OffsetTicks);
            const string sourceTracks = "SELECT id FROM tracks WHERE session_id=$source";
            foreach (var (from, into) in merge.SpeakerMerges)
            {
                Execute(connection, "UPDATE segments SET speaker_id=$into WHERE session_id=$source AND speaker_id=$from", ("$into", into), ("$from", from), source);
                Execute(connection, $"UPDATE turns SET speaker_id=$into WHERE speaker_id=$from AND track_id IN ({sourceTracks})", ("$into", into), ("$from", from), source);
                Execute(connection, $"UPDATE speaker_hints SET speaker_id=$into WHERE speaker_id=$from AND track_id IN ({sourceTracks})", ("$into", into), ("$from", from), source);
                Execute(connection, "DELETE FROM speakers WHERE session_id=$source AND id=$from", ("$from", from), source);
            }
            foreach (var (id, name) in merge.SpeakerNames)
                Execute(connection, "UPDATE speakers SET name=$name WHERE session_id=$source AND id=$id", ("$name", name), ("$id", id), source);
            // The same speaker ID in both (e.g. the same WebVTT file imported twice) is the same speaker.
            Execute(connection, "DELETE FROM speakers WHERE session_id=$source AND id IN (SELECT id FROM speakers WHERE session_id=$target)", source, target);
            Execute(connection, "UPDATE speakers SET session_id=$target WHERE session_id=$source", source, target);
            Execute(connection, $"UPDATE turns SET start_ticks=start_ticks+$offset,end_ticks=end_ticks+$offset WHERE track_id IN ({sourceTracks})", source, offset);
            Execute(connection, $"UPDATE speaker_hints SET start_ticks=start_ticks+$offset,end_ticks=end_ticks+$offset WHERE track_id IN ({sourceTracks})", source, offset);
            foreach (var (id, metadata) in merge.ArchiveChunks)
                Execute(connection, "UPDATE archive_chunks SET start_ticks=start_ticks+$offset,metadata=$metadata WHERE id=$id",
                    ("$id", id), ("$metadata", metadata), offset);
            Execute(connection, "UPDATE normalized_chunks SET session_id=$target,start_ticks=start_ticks+$offset WHERE session_id=$source", source, target, offset);
            foreach (var (id, metadata) in merge.NormalizedChunks)
                Execute(connection, "UPDATE normalized_chunks SET metadata=$metadata WHERE id=$id", ("$id", id), ("$metadata", metadata));
            // Invalidate leases: a response still in flight for the old session is rejected and the job simply runs again.
            Execute(connection, """
                UPDATE jobs SET session_id=$target,state=CASE state WHEN 'Running' THEN 'Pending' ELSE state END,lease=NULL,lease_until=NULL
                WHERE session_id=$source
                """, source, target);
            Execute(connection, "UPDATE segments SET session_id=$target,start_ticks=start_ticks+$offset,end_ticks=end_ticks+$offset WHERE session_id=$source",
                source, target, offset);
            foreach (var (id, name, metadata) in merge.Tracks)
                Execute(connection, "UPDATE tracks SET name=$name,metadata=$metadata WHERE id=$id AND session_id=$source",
                    ("$id", id), ("$name", name), ("$metadata", metadata), source);
            Execute(connection, "UPDATE tracks SET session_id=$target WHERE session_id=$source", source, target);
            if (merge.RegistryJson is not null)
                Execute(connection, "UPDATE sessions SET registry=$registry WHERE id=$target", ("$registry", merge.RegistryJson), target);
            Execute(connection, "UPDATE merged_folders SET session_id=$target WHERE session_id=$source", source, target);
            Execute(connection, "INSERT INTO merged_folders(session_id,directory) VALUES($target,$directory)", target, ("$directory", directory));
            Execute(connection, "DELETE FROM sessions WHERE id=$source", source);
            transaction.Commit();
        }
    }

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

    public bool HasJob(Guid chunkId, string providerId) =>
        Read("SELECT EXISTS(SELECT 1 FROM jobs WHERE chunk_id=$chunk AND provider=$provider)",
            r => r.GetInt64(0) != 0, ("$chunk", chunkId), ("$provider", providerId)).Single();

    // provider/excludeProvider let independent scheduler lanes (e.g. speech vs. speaker analysis) claim disjoint work.
    public StoredJob? ClaimNextJob(string? provider = null, string? excludeProvider = null)
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
                AND ($provider IS NULL OR j.provider=$provider)
                AND ($exclude IS NULL OR j.provider<>$exclude)
                ORDER BY j.rowid LIMIT 1
                """, ("$now", now), ("$provider", provider), ("$exclude", excludeProvider));
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
            Execute(connection, """
                UPDATE segments SET speaker_id=(SELECT mic_speaker FROM sessions WHERE id=$session),uncertain=0,manual_speaker=1,voice_fill=0
                WHERE job_id=$id AND (SELECT mic_speaker FROM sessions WHERE id=$session) IS NOT NULL
                AND (SELECT kind FROM tracks WHERE id=$track)='Microphone'
                """, ("$id", job.Id), ("$session", job.SessionId), ("$track", job.TrackId));
            ApplySpeakerHints(connection, job.Id, job.TrackId);
            transaction.Commit();
            return true;
        }
    }

    // Speakers the user set by hand before the track was re-cut into new chunks carry over to the new lines by time.
    private static void ApplySpeakerHints(SqliteConnection connection, Guid jobId, Guid trackId)
    {
        var rows = new List<(string Id, long Start, long End)>();
        using (var command = Command(connection, "SELECT id,start_ticks,end_ticks FROM segments WHERE job_id=$job AND manual_speaker=0", ("$job", jobId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        if (rows.Count == 0) return;
        var hints = new List<(long Start, long End, string? Speaker)>();
        using (var command = Command(connection,
                   "SELECT start_ticks,end_ticks,speaker_id FROM speaker_hints WHERE track_id=$track AND start_ticks<=$end AND end_ticks>=$start",
                   ("$track", trackId), ("$start", rows.Min(row => row.Start)), ("$end", rows.Max(row => row.End))))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) hints.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        if (hints.Count == 0) return;
        foreach (var row in rows)
        {
            var best = hints.Select(hint => (hint.Speaker, Overlap: Math.Min(row.End, hint.End) - Math.Max(row.Start, hint.Start),
                Contains: hint.Start <= row.Start && row.Start <= hint.End)).MaxBy(item => item.Overlap);
            var matched = row.End > row.Start ? best.Overlap * 2 >= row.End - row.Start : best.Contains;
            if (!matched) continue;
            Execute(connection, "UPDATE segments SET speaker_id=$speaker,uncertain=0,manual_speaker=1,voice_fill=0 WHERE id=$id",
                ("$speaker", best.Speaker), ("$id", row.Id));
        }
    }

    /// <summary>
    /// Replaces each track's normalized chunks with a new cut of the same audio, dropping their jobs and recognized lines.
    /// Lines whose speaker the user set by hand are kept as hints that label the new lines covering the same time.
    /// </summary>
    public void ReplaceTrackChunks(IReadOnlyDictionary<Guid, IReadOnlyList<StoredAudioChunk>> chunksByTrack)
    {
        ArgumentNullException.ThrowIfNull(chunksByTrack);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var (trackId, chunks) in chunksByTrack)
            {
                (string, object?) track = ("$track", trackId);
                const string jobs = "SELECT id FROM jobs WHERE track_id=$track";
                // Hints already turned into lines are refreshed from the lines' current speakers; pending ones are kept.
                Execute(connection, """
                    DELETE FROM speaker_hints WHERE track_id=$track AND EXISTS(SELECT 1 FROM segments s WHERE s.track_id=$track
                        AND s.job_id IS NOT NULL AND s.start_ticks<speaker_hints.end_ticks AND s.end_ticks>speaker_hints.start_ticks)
                    """, track);
                Execute(connection, """
                    INSERT INTO speaker_hints(track_id,start_ticks,end_ticks,speaker_id)
                    SELECT track_id,start_ticks,end_ticks,speaker_id FROM segments WHERE track_id=$track AND job_id IS NOT NULL AND manual_speaker=1
                    """, track);
                Execute(connection, $"DELETE FROM job_attempts WHERE job_id IN ({jobs})", track);
                Execute(connection, $"DELETE FROM results WHERE job_id IN ({jobs})", track);
                Execute(connection, $"DELETE FROM segments WHERE job_id IN ({jobs})", track);
                Execute(connection, "DELETE FROM jobs WHERE track_id=$track", track);
                Execute(connection, "DELETE FROM normalized_chunks WHERE track_id=$track", track);
                foreach (var chunk in chunks)
                {
                    if (chunk.TrackId != trackId) throw new ArgumentException("A replacement chunk belongs to another track.");
                    Execute(connection, """
                        INSERT INTO normalized_chunks(id,session_id,track_id,path,start_sample,sample_count,start_ticks,metadata)
                        VALUES($id,$session,$track,$path,$start,$count,$ticks,$metadata)
                        """, ("$id", chunk.Id), ("$session", chunk.SessionId), track, ("$path", chunk.Path),
                        ("$start", chunk.StartSample), ("$count", chunk.SampleCount), ("$ticks", chunk.StartTicks), ("$metadata", chunk.MetadataJson));
                }
            }
            transaction.Commit();
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

    public IEnumerable<TranscriptRow> EnumerateTranscript(Guid sessionId, string? search = null, string? speakerId = null)
    {
        TranscriptCursor? cursor = null;
        while (true)
        {
            var page = GetTranscriptPage(sessionId, search, speakerId, cursor, 1000);
            foreach (var row in page) yield return row;
            if (page.Count < 1000) yield break;
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

    public long GetLastSegmentEndTicks(Guid sessionId) =>
        Read("SELECT coalesce(max(end_ticks),0) FROM segments WHERE session_id=$session", r => r.GetInt64(0), ("$session", sessionId)).Single();

    public int CountJobSegments(Guid jobId) =>
        Read("SELECT count(*) FROM segments WHERE job_id=$job", r => (int)r.GetInt64(0), ("$job", jobId)).Single();

    public void CorrectSegment(string id, string? correction) =>
        Write("UPDATE segments SET correction=$text WHERE id=$id", ("$text", correction), ("$id", id));

    /// <summary>
    /// Records who was speaking on a track over a time range (for example a Discord user). Lines already transcribed there that
    /// you didn't label get this speaker now; lines transcribed later get it when they arrive. Returns the lines labeled now.
    /// </summary>
    public IReadOnlyList<string> AddSpeakerHint(Guid trackId, long startTicks, long endTicks, string speakerId)
    {
        if (endTicks <= startTicks) return [];
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, "INSERT INTO speaker_hints(track_id,start_ticks,end_ticks,speaker_id) VALUES($track,$start,$end,$speaker)",
                ("$track", trackId), ("$start", startTicks), ("$end", endTicks), ("$speaker", speakerId));
            var rows = new List<(string Id, long Start, long End)>();
            using (var command = Command(connection,
                       "SELECT id,start_ticks,end_ticks FROM segments WHERE track_id=$track AND manual_speaker=0 AND start_ticks<=$end AND end_ticks>=$start",
                       ("$track", trackId), ("$start", startTicks), ("$end", endTicks)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            var labeled = new List<string>();
            foreach (var row in rows)
            {
                var overlap = Math.Min(row.End, endTicks) - Math.Max(row.Start, startTicks);
                if (row.End > row.Start ? overlap * 2 < row.End - row.Start : row.Start < startTicks || row.Start > endTicks) continue;
                Execute(connection, "UPDATE segments SET speaker_id=$speaker,uncertain=0,manual_speaker=1,voice_fill=0 WHERE id=$id",
                    ("$speaker", speakerId), ("$id", row.Id));
                labeled.Add(row.Id);
            }
            transaction.Commit();
            return labeled;
        }
    }

    /// <summary>Lines of a track labeled with this speaker by you or by a speaker hint.</summary>
    public IReadOnlyList<string> GetLabeledSegmentIds(Guid trackId, string speakerId) =>
        Read("SELECT id FROM segments WHERE track_id=$track AND speaker_id=$speaker AND manual_speaker=1 ORDER BY start_ticks",
            r => r.GetString(0), ("$track", trackId), ("$speaker", speakerId));

    public void AssignSpeaker(string id, string? speakerId) =>
        Write("UPDATE segments SET speaker_id=$speaker,uncertain=0,manual_speaker=1,voice_fill=0 WHERE id=$id", ("$speaker", speakerId), ("$id", id));

    public void AssignSpeaker(Guid sessionId, IReadOnlyCollection<string> segmentIds, string? speakerId)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var id in segmentIds)
                Execute(connection, "UPDATE segments SET speaker_id=$speaker,uncertain=0,manual_speaker=1,voice_fill=0 WHERE id=$id AND session_id=$session",
                    ("$speaker", speakerId), ("$id", id), ("$session", sessionId));
            transaction.Commit();
        }
    }

    public IReadOnlyList<TranscriptRow> GetSegments(Guid sessionId, IReadOnlyCollection<string> segmentIds)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        var rows = new List<TranscriptRow>();
        foreach (var id in segmentIds)
            rows.AddRange(Read(TranscriptSelect + " WHERE t.session_id=$session AND t.id=$id", ReadTranscript,
                ("$session", sessionId), ("$id", id)));
        return rows.OrderBy(row => row.StartTicks).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Creates a user-named speaker with no voice profile yet.</summary>
    public StoredSpeaker CreateSpeaker(Guid sessionId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A speaker name is required.");
        var speaker = new StoredSpeaker(Guid.NewGuid().ToString("D"), sessionId, name.Trim(), null, "Named by you");
        UpsertSpeaker(speaker);
        return speaker;
    }

    /// <summary>Folds one speaker into another: rows and turns move over, the source row is removed.</summary>
    public void MergeSpeakers(Guid sessionId, string fromSpeakerId, string intoSpeakerId, string name, string? registryJson)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A speaker name is required.");
        if (fromSpeakerId == intoSpeakerId) throw new ArgumentException("A speaker cannot be merged into itself.");
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, "UPDATE sessions SET mic_speaker=$into WHERE id=$session AND mic_speaker=$from",
                ("$into", intoSpeakerId), ("$from", fromSpeakerId), ("$session", sessionId));
            Execute(connection, "UPDATE segments SET speaker_id=$into WHERE session_id=$session AND speaker_id=$from",
                ("$into", intoSpeakerId), ("$from", fromSpeakerId), ("$session", sessionId));
            Execute(connection, "UPDATE turns SET speaker_id=$into WHERE speaker_id=$from AND track_id IN (SELECT id FROM tracks WHERE session_id=$session)",
                ("$into", intoSpeakerId), ("$from", fromSpeakerId), ("$session", sessionId));
            Execute(connection, "UPDATE speaker_hints SET speaker_id=$into WHERE speaker_id=$from AND track_id IN (SELECT id FROM tracks WHERE session_id=$session)",
                ("$into", intoSpeakerId), ("$from", fromSpeakerId), ("$session", sessionId));
            Execute(connection, "DELETE FROM speakers WHERE session_id=$session AND id=$from", ("$from", fromSpeakerId), ("$session", sessionId));
            if (Execute(connection, "UPDATE speakers SET name=$name WHERE session_id=$session AND id=$into",
                    ("$name", name.Trim()), ("$into", intoSpeakerId), ("$session", sessionId)) != 1)
                throw new InvalidOperationException("The speaker to keep does not exist.");
            if (registryJson is not null)
                Execute(connection, "UPDATE sessions SET registry=$registry WHERE id=$session", ("$registry", registryJson), ("$session", sessionId));
            transaction.Commit();
        }
    }

    /// <summary>
    /// Labels every microphone line of the session with this speaker (as if set by the user), and lines transcribed
    /// later too. Null stops labeling new lines and leaves existing ones as they are. Returns the lines relabeled.
    /// </summary>
    public int SetMicrophoneSpeaker(Guid sessionId, string? speakerId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (Execute(connection, "UPDATE sessions SET mic_speaker=$speaker WHERE id=$session", ("$speaker", speakerId), ("$session", sessionId)) != 1)
                throw new InvalidOperationException("The session does not exist.");
            var count = speakerId is null ? 0 : Execute(connection, """
                UPDATE segments SET speaker_id=$speaker,uncertain=0,manual_speaker=1,voice_fill=0
                WHERE session_id=$session AND track_id IN (SELECT id FROM tracks WHERE session_id=$session AND kind='Microphone')
                """, ("$speaker", speakerId), ("$session", sessionId));
            transaction.Commit();
            return count;
        }
    }

    public IReadOnlyList<StoredJob> GetProviderJobs(Guid sessionId, string providerId) =>
        Read("SELECT * FROM jobs WHERE session_id=$session AND provider=$provider ORDER BY rowid", ReadJob,
            ("$session", sessionId), ("$provider", providerId));

    /// <summary>Runs completed jobs again (for example speaker analysis after the voice profiles improved).</summary>
    public int RequeueJobs(IReadOnlyCollection<Guid> jobIds)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var count = 0;
            foreach (var id in jobIds)
                count += Execute(connection, """
                    UPDATE jobs SET state='Pending',next_attempt=0,error=NULL,lease=NULL,lease_until=NULL
                    WHERE id=$id AND state='Succeeded'
                    """, ("$id", id));
            transaction.Commit();
            return count;
        }
    }

    public void ApplyAutomaticSpeakerAssignments(IReadOnlyList<(string SegmentId, string? SpeakerId, bool Uncertain)> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var assignment in assignments)
                Execute(connection, """
                    UPDATE segments SET speaker_id=$speaker,uncertain=$uncertain WHERE id=$id AND manual_speaker=0 AND voice_fill=0
                    """, ("$speaker", assignment.SpeakerId), ("$uncertain", assignment.Uncertain), ("$id", assignment.SegmentId));
            transaction.Commit();
        }
    }

    /// <summary>Sets speakers found by matching each line's voice to the lines the user labeled. Manual rows are never changed.</summary>
    public int ApplyVoiceFill(Guid sessionId, IReadOnlyList<(string SegmentId, string SpeakerId)> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var count = 0;
            foreach (var (segment, speaker) in assignments)
                count += Execute(connection, """
                    UPDATE segments SET speaker_id=$speaker,uncertain=0,voice_fill=1
                    WHERE id=$id AND session_id=$session AND manual_speaker=0
                    """, ("$speaker", speaker), ("$id", segment), ("$session", sessionId));
            transaction.Commit();
            return count;
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

    public void SetSpeakerParticipant(Guid sessionId, string speakerId, string? participantId) =>
        Write("UPDATE speakers SET participant_id=$participant WHERE session_id=$session AND id=$id",
            ("$participant", participantId), ("$session", sessionId), ("$id", speakerId));

    public const string VoiceLinkPrefix = "voice:";

    public IReadOnlyList<StoredVoice> GetVoices() =>
        Read("SELECT * FROM voices ORDER BY name COLLATE NOCASE", r => new StoredVoice(G(r, "id"), S(r, "name"), S(r, "model"),
            System.Text.Json.JsonSerializer.Deserialize<StoredVoiceSample[]>(S(r, "samples")) ?? [],
            DateTimeOffset.Parse(S(r, "created"), CultureInfo.InvariantCulture), DateTimeOffset.Parse(S(r, "updated"), CultureInfo.InvariantCulture)));

    public void SaveVoice(StoredVoice voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        if (string.IsNullOrWhiteSpace(voice.Name)) throw new ArgumentException("A voice name is required.");
        Write("""
            INSERT INTO voices(id,name,model,samples,created,updated) VALUES($id,$name,$model,$samples,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,model=excluded.model,samples=excluded.samples,updated=excluded.updated
            """, ("$id", voice.Id), ("$name", voice.Name.Trim()), ("$model", voice.ModelId),
            ("$samples", System.Text.Json.JsonSerializer.Serialize(voice.Samples)), ("$created", voice.CreatedUtc), ("$updated", voice.UpdatedUtc));
    }

    /// <summary>Deletes a remembered voice; session speakers keep their names but are no longer linked to it.</summary>
    public void DeleteVoice(Guid voiceId, Guid? relinkTo = null)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, "DELETE FROM voices WHERE id=$id", ("$id", voiceId));
            Execute(connection, "UPDATE speakers SET participant_id=$to WHERE participant_id=$from",
                ("$from", VoiceLinkPrefix + voiceId.ToString("D")), ("$to", relinkTo is { } to ? VoiceLinkPrefix + to.ToString("D") : null));
            transaction.Commit();
        }
    }

    public int DeleteAllVoices()
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var count = Execute(connection, "DELETE FROM voices");
            Execute(connection, "UPDATE speakers SET participant_id=NULL WHERE participant_id LIKE 'voice:%'");
            transaction.Commit();
            return count;
        }
    }

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
            S(r, "processing_state"), N(r, "mic_speaker"));
    private static StoredAudioChunk ReadChunk(SqliteDataReader r) =>
        new(G(r, "id"), G(r, "session_id"), G(r, "track_id"), S(r, "path"), L(r, "start_sample"),
            (int)L(r, "sample_count"), L(r, "start_ticks"), S(r, "metadata"));
    private static StoredJob ReadJob(SqliteDataReader r) =>
        new(G(r, "id"), G(r, "session_id"), G(r, "track_id"), G(r, "chunk_id"), S(r, "provider"), S(r, "language"),
            L(r, "cloud") != 0, S(r, "state"), (int)L(r, "attempts"), N(r, "lease"), N(r, "error"));
    private static TranscriptRow ReadTranscript(SqliteDataReader r) =>
        new(S(r, "id"), G(r, "session_id"), G(r, "track_id"), L(r, "start_ticks"), L(r, "end_ticks"),
            S(r, "raw_text"), N(r, "correction"), N(r, "speaker_id"), S(r, "speaker_name"), S(r, "granularity"),
            S(r, "provenance"), L(r, "uncertain") != 0, L(r, "manual_speaker") != 0, L(r, "voice_fill") != 0);

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
        CREATE TABLE IF NOT EXISTS merged_folders(session_id TEXT NOT NULL REFERENCES sessions(id),directory TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS speaker_hints(
            track_id TEXT NOT NULL REFERENCES tracks(id),start_ticks INTEGER NOT NULL,end_ticks INTEGER NOT NULL,speaker_id TEXT);
        CREATE INDEX IF NOT EXISTS speaker_hint_time ON speaker_hints(track_id,start_ticks);
        CREATE TABLE IF NOT EXISTS voices(
            id TEXT PRIMARY KEY,name TEXT NOT NULL,model TEXT NOT NULL,samples TEXT NOT NULL,created TEXT NOT NULL,updated TEXT NOT NULL);
        """;
}
