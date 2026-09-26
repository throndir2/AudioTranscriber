using AudioTranscriber.Integrations;
using AudioTranscriber.Storage;

namespace AudioTranscriber.Application;

public sealed record ProviderOption(string Id, string Name, bool IsCloud, string TimingDescription);
public sealed record DeviceChoice(string Id, string Name);
public sealed record MediaStreamChoice(int Index, string Description);
public sealed record MediaProbeSummary(string Path, double DurationSeconds, IReadOnlyList<MediaStreamChoice> Streams);
public sealed record AppNotification(string Message, bool IsError = false);
public sealed record CaptureMeter(double Output, double Microphone);

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
    bool ModelSetupRunning { get; }
    Task EnsureDefaultModelsAsync();
    void RetryBlockedLocalWork();
    Task InstallRecommendedWhisperModelAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task InstallDiarizationModelsAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<MediaProbeSummary> ProbeMediaAsync(string path, CancellationToken cancellationToken = default);
    Task<StoredSession> StartRecordingAsync(string name, string outputDeviceId, string? microphoneDeviceId,
        string providerId, string language, bool cloudConsent, bool reduceEcho = true, CancellationToken cancellationToken = default);
    Task StopRecordingAsync(CancellationToken cancellationToken = default);
    Task<StoredSession> ImportAudioAsync(string name, string path, int streamIndex, string providerId,
        string language, bool cloudConsent, CancellationToken cancellationToken = default);
    Task ImportVttAsync(Guid sessionId, string path, CancellationToken cancellationToken = default);
    Task FetchTeamsTranscriptAsync(Guid sessionId, TeamsTranscriptRequest request,
        Func<DeviceSignInPrompt, Task> showSignIn, CancellationToken cancellationToken = default);
    Task DiarizeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
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
    Task PlayAsync(Guid sessionId, Guid trackId, long sessionTicks, CancellationToken cancellationToken = default);
    void StopPlayback();
}
