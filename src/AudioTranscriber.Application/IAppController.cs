using AudioTranscriber.Integrations;
using AudioTranscriber.Storage;

namespace AudioTranscriber.Application;

public sealed record ProviderOption(string Id, string Name, bool IsCloud, string TimingDescription);
public sealed record DeviceChoice(string Id, string Name);
public sealed record MediaStreamChoice(int Index, string Description);
public sealed record MediaProbeSummary(string Path, double DurationSeconds, IReadOnlyList<MediaStreamChoice> Streams);
public sealed record AppNotification(string Message, bool IsError = false);
public sealed record CaptureMeter(double Output, double Microphone);
public sealed record SessionDeletion(int Deleted, IReadOnlyList<string> LeftoverFolders);
// Result of matching unlabeled lines to the speakers of the lines the user labeled.
public sealed record SpeakerFillSummary(int Labeled, int Filled, int Changed, int Unmatched, int Skipped, int? AgreementPercent,
    IReadOnlyList<(string Name, int Lines)> PerSpeaker);

public interface IAppController : IAsyncDisposable
{
    LibraryStore Store { get; }
    IReadOnlyList<ProviderOption> Providers { get; }
    bool IsRecording { get; }
    Guid? RecordingSessionId { get; }
    bool HasNvidiaKey { get; }
    bool DiarizationModelsReady { get; }
    event Action<AppNotification>? Notification;
    event Action<CaptureMeter>? LevelsChanged;
    // Raised (on a background thread) when a session's transcript rows or speaker labels change.
    event Action<Guid>? TranscriptChanged;
    IReadOnlyList<DeviceChoice> GetOutputDevices();
    IReadOnlyList<DeviceChoice> GetMicrophoneDevices();
    void SetNvidiaKey(string key, bool remember);
    void ClearNvidiaKey();
    void SetLocalWhisperModel(string path);
    string? WhisperModelPath { get; }
    string? SetupStatus { get; }
    // True when the default local Parakeet model is installed.
    bool ParakeetModelReady { get; }
    Task InstallParakeetModelAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    // What the local NVIDIA GPU check found, and whether Parakeet is running on it.
    string? LocalGpuStatus { get; }
    // Set when this PC's NVIDIA GPU can run Parakeet and the user hasn't decided yet (a one-time, large download).
    string? GpuOffer { get; }
    bool? GpuParakeetEnabled { get; }
    void SetGpuParakeet(bool enabled);
    bool ModelSetupRunning { get; }
    Task EnsureDefaultModelsAsync();
    void RetryBlockedLocalWork();
    Task InstallRecommendedWhisperModelAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task InstallDiarizationModelsAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<MediaProbeSummary> ProbeMediaAsync(string path, CancellationToken cancellationToken = default);
    Task<StoredSession> StartRecordingAsync(string name, string outputDeviceId, string? microphoneDeviceId,
        string providerId, string language, bool cloudConsent, bool reduceEcho = true, CancellationToken cancellationToken = default);
    // Records more audio into an existing session, placed after everything it already holds.
    Task<StoredSession> ContinueRecordingAsync(Guid sessionId, string outputDeviceId, string? microphoneDeviceId,
        bool reduceEcho = true, CancellationToken cancellationToken = default);
    // Stitches sessions into the earliest one, in recording order, on one timeline. Returns the merged session.
    Task<StoredSession> MergeSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken = default);
    Task StopRecordingAsync(CancellationToken cancellationToken = default);
    Task<StoredSession> ImportAudioAsync(string name, string path, int streamIndex, string providerId,
        string language, bool cloudConsent, CancellationToken cancellationToken = default);
    Task ImportVttAsync(Guid sessionId, string path, CancellationToken cancellationToken = default);
    Task FetchTeamsTranscriptAsync(Guid sessionId, TeamsTranscriptRequest request,
        Func<DeviceSignInPrompt, Task> showSignIn, CancellationToken cancellationToken = default);
    Task DiarizeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    // Gives every line you didn't label the labeled speaker it sounds closest to; your own labels are never changed.
    Task<SpeakerFillSummary> FillSpeakersFromLabelsAsync(Guid sessionId, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
    // Finds a session speaker by name (case-insensitive) or creates a user-named one.
    StoredSpeaker GetOrCreateSpeaker(Guid sessionId, string name);
    // Manually labels rows (null = Unknown). With speaker models installed, the rows' voice is learned in the background.
    void AssignSpeaker(Guid sessionId, IReadOnlyCollection<string> segmentIds, string? speakerId);
    // Renames a speaker; a name another speaker already has merges the two. Returns the surviving speaker ID.
    Task<string> RenameSpeakerAsync(Guid sessionId, string speakerId, string name, CancellationToken cancellationToken = default);
    void PauseTranscription(Guid sessionId);
    void ResumeTranscription(Guid sessionId);
    void CancelTranscription(Guid sessionId);
    void SetCloudConsent(Guid sessionId, bool consent);
    // Permanently removes sessions: stops their work, then deletes their rows and retained audio. Refuses the recording session.
    Task<SessionDeletion> DeleteSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken = default);
    Task PlayAsync(Guid sessionId, Guid trackId, long sessionTicks, CancellationToken cancellationToken = default);
    void StopPlayback();
}
