namespace AudioTranscriber.Storage;

public sealed record StoredSession(
    Guid Id, string Name, string Directory, DateTimeOffset CreatedUtc, string State,
    bool CloudConsent, string ProviderId, string Language, string? Error, long DurationTicks,
    string ProcessingState = "Running");

public sealed record StoredTrack(
    Guid Id, Guid SessionId, string Kind, string Name, string? OriginalPath,
    int AudioStreamIndex, string? MetadataJson);

public sealed record StoredAudioChunk(
    Guid Id, Guid SessionId, Guid TrackId, string Path, long StartSample,
    int SampleCount, long StartTicks, string MetadataJson);

public sealed record StoredJob(
    Guid Id, Guid SessionId, Guid TrackId, Guid ChunkId, string ProviderId,
    string Language, bool Cloud, string State, int Attempts, string? LeaseToken, string? Error);

public sealed record TranscriptCursor(long StartTicks, string Id);
public sealed record TranscriptRow(
    string Id, Guid SessionId, Guid TrackId, long StartTicks, long EndTicks,
    string RawText, string? Correction, string? SpeakerId, string SpeakerName,
    string TimingGranularity, string Provenance, bool Uncertain, bool ManualSpeaker = false, bool VoiceFilled = false)
{
    public string Text => Correction ?? RawText;
    public string Timestamp => $"{(long)TimeSpan.FromTicks(StartTicks).TotalHours:00}:{TimeSpan.FromTicks(StartTicks).Minutes:00}:{TimeSpan.FromTicks(StartTicks).Seconds:00}";
}

public sealed record SegmentDraft(
    long StartTicks, long EndTicks, string Text, string? SpeakerId,
    string TimingGranularity, string Provenance, bool Uncertain = false);

public sealed record StoredSpeaker(string Id, Guid SessionId, string Name, string? ParticipantId, string Provenance);
public sealed record StoredTurn(Guid TrackId, long StartTicks, long EndTicks, string? SpeakerId, bool Overlap, bool Uncertain);
// Row rewrites for merging Source into Target; metadata values are already shifted by OffsetTicks.
public sealed record SessionMerge(
    Guid TargetId, Guid SourceId, long OffsetTicks,
    IReadOnlyList<(Guid Id, string Name, string? Metadata)> Tracks,
    IReadOnlyList<(Guid Id, string Metadata)> ArchiveChunks,
    IReadOnlyList<(Guid Id, string Metadata)> NormalizedChunks,
    IReadOnlyDictionary<string, string> SpeakerNames,
    IReadOnlyDictionary<string, string> SpeakerMerges,
    string? RegistryJson);

public sealed record QueueProgress(int Pending, int Running, int Succeeded, int Failed, int Paused);
