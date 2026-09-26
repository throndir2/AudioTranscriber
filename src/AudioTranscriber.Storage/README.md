# Storage integration

Existing callers remain compatible. Session records additionally expose their
durable processing state.

## Persistent processing controls

Schema version 4 adds `sessions.processing_state`. Pause and Cancel update this
state together with existing jobs. Newly queued chunks inherit the suspension,
and claims require Running, so continued capture or normalization cannot resume
recognition behind the user's back. Only explicit `ResumeJobs` reopens the
session gate. Provider-specific resume does not bypass it.

`TryRenewLease` returns false for an expired or no-longer-owned lease.
`CloseConnections` clears only this library's connection pool after its workers
have stopped; this releases file handles without disturbing another library.

## Diarization jobs

Queue local diarization through `QueueTranscription(chunk, "local-diarization",
language, cloud: false)`. No separate job kind is required.

- `CompleteDiarizationJob(job, registryJson, speakers, turns, startTicks, endTicks)`
  returns `true` only for the matching, running, unexpired lease. Job completion,
  registry replacement, speaker upserts, and turn replacement commit together.
  Database failures roll everything back and propagate to the caller. A `false`
  result must not be treated as an accepted result.
- Speakers must belong to the job's session. Turns must belong to its track and
  fit within the supplied interval. Existing turns with starts in
  `[startTicks, endTicks)` are replaced; other turns are retained.
- Upserts preserve existing speaker names and provenance, including user
  renames, while accepting a newly supplied participant identifier.
- `HasCompletedJob(chunkId, providerId)` checks successful jobs across languages.
  Check it before queuing another analysis of the same evidence after restart.
- `ResumeProviderJobs(sessionId, providerId)` resets only that provider's
  `Paused`, `Blocked`, and `Failed` jobs. It preserves attempt counts, canceled
  and successful jobs, active leases, and scheduled retry backoff.
- `ResumeJobs(sessionId)` is the explicit user-resume operation and additionally
  resumes `Canceled` jobs. It clears old leases and retry errors without resetting
  attempt counts or replaying successful jobs.
- `SetSessionDuration(sessionId, ticks)` requires nonnegative ticks and increases
  duration monotonically, so an older capture checkpoint cannot truncate
  already indexed audio.

## Raw evidence and account-wide cloud blocking

Call `SaveRawAttempt(job, rawJson, observedModel)` immediately after receiving a
provider response, **before merging or validating its timing**. Schema version 3
adds `job_attempts`, keyed by `(job_id, attempt)`, independently of successful
`results`. Each attempt retains its first response and model value; duplicate
saves cannot overwrite earlier evidence. `observedModel` may be null when not
returned. Supply only the sanitized Core evidence envelope, never credentials
or authorization headers.

Raw evidence can be retained after a lease expires or a job is canceled, but
saving it never changes the job state, lease, transcript, or completed results.
Database errors propagate. Successful `CompleteJob` calls still store their
normal result independently; a failed merge or retry cannot erase prior attempts.

`BlockCloudJobs(safeError)` blocks pending, retry-waiting, and running cloud jobs
across all sessions, clears their leases, and retains the safe account error.
This prevents late completion from a revoked running lease. Local jobs and
already paused, failed, canceled, or successful jobs are unchanged. No cloud job
is automatically resumed; the user must explicitly resume the desired session,
and the existing consent gate still applies.

## Automatic and manual speaker assignments

`ApplyAutomaticSpeakerAssignments` accepts an
`IReadOnlyList<(string SegmentId, string? SpeakerId, bool Uncertain)>` and commits
the batch atomically. It changes only rows not marked as manually assigned.
`AssignSpeaker` marks a row manual even when the user explicitly selects Unknown
(`null`); the session overload labels a batch atomically. Neither path changes raw
text or corrections. `CreateSpeaker` adds a user-named speaker without a voice
profile. `MergeSpeakers` moves one speaker's rows and turns to another, removes the
merged speaker row, renames the kept one, and optionally replaces the registry JSON,
all in one transaction. `TranscriptRow.ManualSpeaker` exposes the manual flag.

Schema version 2 adds `segments.manual_speaker`. Version 1 did not record the
origin of speaker assignments, so migration conservatively protects **all
existing rows**, including Unknown, rather than guessing which choices were
manual. Rows added after migration allow automatic assignment until explicitly
edited. Version 3 preserves those flags while adding attempt history.
Initialization and migration are transactional and safe against concurrent
opens; newer schema versions are rejected.

See [native runtime provenance](NativeSqlite/README.md) for the verified SQLite
asset and the limitations of the Windows image-loading diagnosis.
