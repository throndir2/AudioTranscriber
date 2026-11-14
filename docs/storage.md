# Session storage

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Users and contributors
**Last verified:** 2026-11-14 against b3ed981
**Code:** `src/AudioTranscriber.Storage`, `src/AudioTranscriber.Application`, `src/AudioTranscriber.App`

The library stores session records in SQLite and stores audio files separately
in the library's session folders. The app uses this data to restore sessions,
transcripts, jobs, and speaker profiles.

## Library location and ownership

The default desktop library is `%LOCALAPPDATA%\AudioTranscriber` on Windows.
On Linux, the application appends `AudioTranscriber` to the platform's local
application data directory. Use `--data-root PATH` to select another directory.
The application creates `library.sqlite3` and `library.lock` there.

The app opens `library.lock` with exclusive access. Do not start two app
processes against the same library at the same time.

## SQLite contents

`LibraryStore` stores the session and track catalog, audio-file references,
normalized audio chunks, transcription jobs and attempts, provider results,
transcript rows and corrections, speaker identities and turns, and remembered
voice samples. Full-text search indexes the effective transcript text: a
correction replaces the raw recognition text for search.

The current schema version is 6. `LibraryStore` applies schema creation and
migrations in a transaction. SQLite uses write-ahead logging and full
synchronous mode. A library from a newer schema version is rejected rather
than downgraded.

## Audio files and session deletion

Original recordings and imported media remain files in session folders.
Normalized PCM chunks also remain files; SQLite stores their paths and timing
metadata. This keeps large audio data out of database rows.

Deleting a session removes its database rows and retained session audio.
Exported transcripts and live transcript files are separate and are not removed
with the session. See [desktop usage](usage.md#closing-safely) and
[disk cleanup](cleanup.md) for related behavior.

## Settings stored outside SQLite

Application preferences, recording defaults, live-file settings, template
settings, and Discord bot settings use separate files in the library directory.
The files are not replacements for the SQLite session catalog. Never edit
credential fields or copy private library data into public bug reports.

For a user-level overview, see [desktop usage](usage.md#session-library-storage).
