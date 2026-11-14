# AudioTranscriber knowledge base

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Users, contributors, and coding agents
**Last verified:** 2026-11-14 against 0b9c0c8
**Code:** `AudioTranscriber.slnx`

Start with a question in `repo-kb`. Read the cited section and follow its **Code:** paths to the source.
The pages describe implemented behavior. Sections marked Planned describe work that is not built.

## Page map

- [Audio](audio.md): How do capture, normalization, archive recovery, and playback work?
- [Application](application.md): Which class owns session state, job scheduling, and recognition coordination?
- [Benchmark procedure](benchmark.md): How does the local model comparison run?
- [Cleanup](cleanup.md): Which generated files can a contributor remove safely?
- [Diarization](diarization.md): How are speaker segments and voice profiles produced?
- [MCP](mcp.md): How do agents control the headless engine and desktop window?
- [Privacy](privacy.md): When can audio leave the computer, and where are credentials stored?
- [Providers](providers.md): How do local and hosted speech providers process recognition requests?
- [Releases](releases.md): How are release versions, packages, and updates built?
- [Storage](storage.md): How do SQLite transactions store sessions, jobs, transcripts, and voice profiles?
- [Usage](usage.md): How does a user record, import, transcribe, edit, and export?
- [Validation](validation.md): Which local validation limits and evidence apply?
- [Benchmarks](benchmarks/README.md): Where are the historical comparison fixtures and results?
- [Images](images/README.md): Where are the user-interface screenshots?
- [Internal](internal/README.md): What is the KB contract and contributor search procedure?
- [User guide pages](wiki/README.md): Where are the short guides for each desktop feature?

## Main code areas

| Area | Source folder | Starting page |
| --- | --- | --- |
| Contracts and records | `src/AudioTranscriber.Core` | [Architecture](wiki/Architecture-and-Conventions.md) |
| Capture, imports, playback | `src/AudioTranscriber.Audio` | [Audio](audio.md) |
| Recognition providers | `src/AudioTranscriber.Providers` | [Providers](providers.md) |
| Speaker inference | `src/AudioTranscriber.Diarization`, `src/AudioTranscriber.Worker` | [Diarization](diarization.md) |
| SQLite and recovery | `src/AudioTranscriber.Storage` | [Storage](storage.md), [Data and recovery](wiki/Data-and-Recovery.md) |
| Job scheduling and state | `src/AudioTranscriber.Application` | [Application](application.md) |
| Desktop, templates, updates | `src/AudioTranscriber.App` | [Usage](usage.md) |
| Transcript outputs | `src/AudioTranscriber.Integrations` | [Output templates](wiki/Output-Templates.md) |
| Discord voice streams | `src/AudioTranscriber.Discord` | [Discord](wiki/Discord.md) |
| Local build and packages | `scripts`, `installer` | [Developer guide](wiki/Developer-Guide.md), [Releases](releases.md) |
| UI automation | `tools/AudioTranscriber.UiMcp` | [MCP](mcp.md) |
| Model comparison | `tools/AudioTranscriber.Benchmarks` | [Benchmark procedure](benchmark.md) |
| Local tests | `tests` | [Validation](validation.md) |
