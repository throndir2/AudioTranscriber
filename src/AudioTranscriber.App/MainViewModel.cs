using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using AudioTranscriber.Application;
using AudioTranscriber.Storage;

namespace AudioTranscriber.App;

public sealed class MainViewModel : ObservableObject
{
    private readonly IAppController controller;
    private readonly DesktopDialogs dialogs;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer refreshTimer;
    private CancellationTokenSource? operationCancellation;
    private Task? activeOperation;
    private Task? stopTask;
    private bool busy, closing, stopping, initialized, refreshingSelection, disposed;
    private string status = "Ready. Nothing is recorded or uploaded until you explicitly start.";
    private bool statusIsError;
    private StoredSession? selectedSession;
    private ProviderOption? selectedProvider;
    private DeviceChoice? outputDevice, microphoneDevice;
    private string sessionName = $"Session {DateTime.Now:yyyy-MM-dd HH:mm}";
    private string language = "en";
    private bool microphoneEnabled, newCloudConsent, selectedCloudConsent;
    private double outputLevel, microphoneLevel;
    private string search = "", seek = "", appliedSearch = "";
    private SpeakerChoice? speakerFilter, assignmentSpeaker;
    private string? appliedSpeaker;
    private long? appliedSeek;
    private readonly List<TranscriptCursor?> cursors = [null];
    private int pageIndex;
    private bool hasNext;
    private TranscriptItem? selectedRow;
    private StoredSpeaker? managedSpeaker;
    private StoredTrack? selectedTrack;
    private string correction = "", speakerName = "", playbackTimestamp = "00:00:00";
    private string queueSummary = "No session selected.";
    private string localModel = "No model selected in this window.";
    private string modelStatus = "";
    private bool rememberKey;
    private int previousSucceeded = -1;
    private CaptureMeter pendingMeter = new(0, 0);
    private int meterQueued;
    private string liveFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioTranscriber", "live-transcript.txt");
    private bool liveFileEnabled;
    private Guid? liveSessionId;
    private string? lastLiveContent;
    private Task? liveWrite;
    private string liveFileStatus = "Live transcript file is off.";

    public MainViewModel(IAppController controller, DesktopDialogs dialogs, Dispatcher dispatcher)
    {
        this.controller = controller;
        this.dialogs = dialogs;
        this.dispatcher = dispatcher;
        Providers = controller.Providers;
        selectedProvider = Providers.FirstOrDefault(x => !x.IsCloud);
        if (selectedProvider is null)
            selectedProvider = Providers.FirstOrDefault();
        RefreshDevicesCommand = new AsyncCommand(() => RunAsync("Enumerating audio endpoints…", _ =>
        {
            RefreshDevices();
            return Task.CompletedTask;
        }), () => !Busy && !IsRecording);
        StartRecordingCommand = new AsyncCommand(StartRecordingAsync, () =>
            !Busy && !IsRecording && !Stopping && OutputDevice is not null && HasNewSessionDetails && !closing);
        StopRecordingCommand = new AsyncCommand(StopRecordingAsync, () => IsRecording && !Stopping && !closing);
        ImportAudioCommand = new AsyncCommand(ImportAudioAsync, () => !Busy && HasNewSessionDetails && !closing);
        ImportVttCommand = new AsyncCommand(ImportVttAsync, CanWorkWithSession);
        FetchTeamsCommand = new AsyncCommand(FetchTeamsAsync, CanWorkWithSession);
        InstallModelsCommand = new AsyncCommand(InstallModelsAsync, () => !Busy && !closing);
        ChooseWhisperModelCommand = new RelayCommand(ChooseWhisperModel, () => !Busy && !closing);
        InstallWhisperModelCommand = new AsyncCommand(InstallWhisperModelAsync, () => !Busy && !closing);
        if (controller.WhisperModelPath is { } currentModel) localModel = DescribeModel(currentModel);
        DiarizeCommand = new AsyncCommand(() => RunForSessionAsync("Running local speaker analysis…",
            (id, token) => controller.DiarizeSessionAsync(id, token)), () => CanWorkWithSession() && controller.DiarizationModelsReady);
        PauseCommand = new RelayCommand(() => SessionAction(controller.PauseTranscription,
            "Transcription paused; recording, if active, continues."), () => SelectedSession is not null && !closing);
        ResumeCommand = new RelayCommand(() => SessionAction(controller.ResumeTranscription,
            "Transcription resumed with the session's provider and consent."), () => SelectedSession is not null && !closing);
        CancelJobsCommand = new RelayCommand(CancelJobs, () => SelectedSession is not null && !closing);
        GrantConsentCommand = new RelayCommand(GrantConsent, () => SelectedSession is not null && SelectedCloudConsent && !closing);
        RevokeConsentCommand = new RelayCommand(() => SessionAction(id => controller.SetCloudConsent(id, false),
            "Cloud consent revoked. Future uploads stop; already sent audio cannot be recalled."), () => SelectedSession is not null && !closing);
        ClearKeyCommand = new RelayCommand(() => Guard(() =>
        {
            controller.ClearNvidiaKey();
            Changed(nameof(KeyStatus));
            SetStatus("NVIDIA key cleared from memory and remembered storage.");
        }));
        RefreshCommand = new RelayCommand(() => Guard(() => { RefreshLibrary(); LoadPage(); }), () => !closing);
        SearchCommand = new RelayCommand(ApplySearch, () => SelectedSession is not null && !closing);
        ClearSearchCommand = new RelayCommand(() =>
        {
            Search = "";
            Seek = "";
            SpeakerFilter = SpeakerFilters.FirstOrDefault();
            ApplySearch();
        }, () => SelectedSession is not null && !closing);
        NextPageCommand = new RelayCommand(NextPage, () => hasNext && !closing);
        PreviousPageCommand = new RelayCommand(() => { pageIndex--; Guard(LoadPage); }, () => pageIndex > 0 && !closing);
        SaveCorrectionCommand = new RelayCommand(SaveCorrection, () => SelectedRow is not null && !closing);
        RestoreRawCommand = new RelayCommand(RestoreRaw, () => SelectedRow?.Row.Correction is not null && !closing);
        RenameSpeakerCommand = new RelayCommand(RenameSpeaker,
            () => SelectedSession is not null && ManagedSpeaker is not null && !string.IsNullOrWhiteSpace(SpeakerName) && !closing);
        AssignSpeakerCommand = new RelayCommand(AssignSpeaker,
            () => SelectedRow is not null && AssignmentSpeaker is not null && !closing);
        PlayRowCommand = new AsyncCommand(PlayRowAsync, () => SelectedRow is not null && !Busy && !closing);
        PlayTrackCommand = new AsyncCommand(PlayTrackAsync, () => SelectedSession is not null && SelectedTrack is not null && !Busy && !closing);
        StopPlaybackCommand = new RelayCommand(() => Guard(controller.StopPlayback), () => !closing);
        ExportCommand = new AsyncCommand(ExportAsync, CanWorkWithSession);
        CancelOperationCommand = new RelayCommand(() => operationCancellation?.Cancel(),
            () => Busy && operationCancellation is not null && !closing);
        BrowseLiveFileCommand = new RelayCommand(() =>
        {
            if (dialogs.SaveLiveTranscript(LiveFilePath) is { } path) LiveFilePath = path;
        }, () => !closing);
        LiveMirrorSelectedCommand = new RelayCommand(() => { if (SelectedSession is { } s) StartLiveFile(s.Id); },
            () => SelectedSession is not null && !closing);
        StopLiveFileCommand = new RelayCommand(StopLiveFile, () => liveSessionId is not null && !closing);
        LoadLiveSettings();
        controller.Notification += OnNotification;
        controller.LevelsChanged += OnLevelsChanged;
        refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
            (_, _) => { if (initialized && !closing) { Guard(RefreshLibrary); UpdateLiveFile(); } }, dispatcher);
        refreshTimer.Stop();
    }

    public ObservableCollection<StoredSession> Sessions { get; } = [];
    public ObservableCollection<DeviceChoice> OutputDevices { get; } = [];
    public ObservableCollection<DeviceChoice> MicrophoneDevices { get; } = [];
    public ObservableCollection<StoredTrack> Tracks { get; } = [];
    public ObservableCollection<StoredSpeaker> Speakers { get; } = [];
    public ObservableCollection<SpeakerChoice> SpeakerFilters { get; } = [];
    public ObservableCollection<SpeakerChoice> AssignmentSpeakers { get; } = [];
    public ObservableCollection<TranscriptItem> Transcript { get; } = [];
    public ObservableCollection<StoredJob> Jobs { get; } = [];
    public IReadOnlyList<ProviderOption> Providers { get; }
    public string DataRoot => controller.Store.RootDirectory;
    public bool IsRecording => controller.IsRecording;
    public bool Stopping { get => stopping; private set { Set(ref stopping, value); Changed(nameof(CaptureState)); } }
    public string CaptureState => Stopping ? "STOPPING · sealing original audio tails" :
        IsRecording ? "● RECORDING · audio is being saved" : "NOT RECORDING";
    public string RecordingSession => controller.RecordingSessionId is { } id
        ? Sessions.FirstOrDefault(x => x.Id == id)?.Name ?? id.ToString() : "";
    public bool Busy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool StatusIsError { get => statusIsError; private set => Set(ref statusIsError, value); }
    public bool HasSession => SelectedSession is not null;
    public string SessionSummary => SelectedSession is { } s
        ? $"{s.State}  ·  Processing: {s.ProcessingState}  ·  {TranscriptPresentation.Duration(s.DurationTicks)}  ·  {s.Language}  ·  {s.ProviderId}" : "Select a session or create one with Record / import.";
    public string SessionError => SelectedSession?.Error ?? "";
    public string ConsentSummary => SelectedSession?.CloudConsent == true ? "Cloud upload consent is ON for this session." : "Cloud upload consent is OFF for this session.";
    public string QueueSummary { get => queueSummary; private set => Set(ref queueSummary, value); }
    public string KeyStatus => controller.HasNvidiaKey ? "A key is available (never displayed)." : "No NVIDIA key. Recording and local work remain available.";
    public string ModelStatus { get => modelStatus; private set => Set(ref modelStatus, value); }
    public string LocalModel { get => localModel; private set => Set(ref localModel, value); }
    public bool RememberKey { get => rememberKey; set => Set(ref rememberKey, value); }
    public string SessionName { get => sessionName; set => Set(ref sessionName, value); }
    public string Language { get => language; set => Set(ref language, value); }
    public bool NewCloudConsent { get => newCloudConsent; set => Set(ref newCloudConsent, value); }
    public bool SelectedCloudConsent { get => selectedCloudConsent; set => Set(ref selectedCloudConsent, value); }
    public bool MicrophoneEnabled { get => microphoneEnabled; set => Set(ref microphoneEnabled, value); }
    public DeviceChoice? OutputDevice { get => outputDevice; set => Set(ref outputDevice, value); }
    public DeviceChoice? MicrophoneDevice { get => microphoneDevice; set => Set(ref microphoneDevice, value); }
    public double OutputLevel { get => outputLevel; private set => Set(ref outputLevel, value); }
    public double MicrophoneLevel { get => microphoneLevel; private set => Set(ref microphoneLevel, value); }
    public ProviderOption? SelectedProvider
    {
        get => selectedProvider;
        set
        {
            if (!Set(ref selectedProvider, value)) return;
            NewCloudConsent = false;
            Changed(nameof(ProviderHelp));
        }
    }
    public string ProviderHelp => SelectedProvider is { } p
        ? $"{(p.IsCloud ? "NVIDIA-hosted. Upload requires this session's consent AND a key." : "Local-only. No automatic cloud fallback.")} {p.TimingDescription}"
        : "Choose a transcription provider.";
    private bool HasNewSessionDetails => !string.IsNullOrWhiteSpace(SessionName) && !string.IsNullOrWhiteSpace(Language) && SelectedProvider is not null;

    public StoredSession? SelectedSession
    {
        get => selectedSession;
        set
        {
            if (refreshingSelection) return;
            var changedId = selectedSession?.Id != value?.Id;
            if (!Set(ref selectedSession, value)) return;
            Changed(nameof(HasSession));
            Changed(nameof(SessionSummary));
            Changed(nameof(SessionError));
            Changed(nameof(ConsentSummary));
            if (!changedId) return;
            SelectedCloudConsent = false;
            previousSucceeded = -1;
            Search = "";
            Seek = "";
            appliedSearch = "";
            appliedSpeaker = null;
            appliedSeek = null;
            ResetPaging();
            Guard(() => { RefreshSessionDetails(); LoadPage(); });
        }
    }
    public string Search { get => search; set => Set(ref search, value); }
    public string Seek { get => seek; set => Set(ref seek, value); }
    public SpeakerChoice? SpeakerFilter { get => speakerFilter; set => Set(ref speakerFilter, value); }
    public SpeakerChoice? AssignmentSpeaker { get => assignmentSpeaker; set => Set(ref assignmentSpeaker, value); }
    public StoredTrack? SelectedTrack { get => selectedTrack; set => Set(ref selectedTrack, value); }
    public string PlaybackTimestamp { get => playbackTimestamp; set => Set(ref playbackTimestamp, value); }
    public string Correction { get => correction; set => Set(ref correction, value); }
    public string SpeakerName { get => speakerName; set => Set(ref speakerName, value); }
    public string PageSummary => $"Page {pageIndex + 1} · {Transcript.Count} of at most {TranscriptPresentation.PageSize} rows loaded";
    public StoredSpeaker? ManagedSpeaker
    {
        get => managedSpeaker;
        set { if (Set(ref managedSpeaker, value)) SpeakerName = value?.Name ?? ""; }
    }
    public TranscriptItem? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            Correction = value?.Text ?? "";
            AssignmentSpeaker = AssignmentSpeakers.FirstOrDefault(x => x.Id == value?.Row.SpeakerId);
            ManagedSpeaker = Speakers.FirstOrDefault(x => x.Id == value?.Row.SpeakerId);
            Changed(nameof(RowDetails));
            Changed(nameof(RawText));
        }
    }
    public string RawText => SelectedRow?.Row.RawText ?? "";
    public string RowDetails => SelectedRow is { } item
        ? $"{item.TrackName}  ·  {item.Timestamp} – {TranscriptPresentation.Duration(item.Row.EndTicks)}  ·  {item.Timing}  ·  {item.Attribution}\nSource: {item.Provenance}"
        : "Select a row to inspect raw recognition, correct text, or play its own audio track.";

    public ICommand RefreshDevicesCommand { get; }
    public ICommand StartRecordingCommand { get; }
    public ICommand StopRecordingCommand { get; }
    public ICommand ImportAudioCommand { get; }
    public ICommand ImportVttCommand { get; }
    public ICommand FetchTeamsCommand { get; }
    public ICommand InstallModelsCommand { get; }
    public ICommand ChooseWhisperModelCommand { get; }
    public ICommand InstallWhisperModelCommand { get; }
    public ICommand DiarizeCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelJobsCommand { get; }
    public ICommand GrantConsentCommand { get; }
    public ICommand RevokeConsentCommand { get; }
    public ICommand ClearKeyCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand SaveCorrectionCommand { get; }
    public ICommand RestoreRawCommand { get; }
    public ICommand RenameSpeakerCommand { get; }
    public ICommand AssignSpeakerCommand { get; }
    public ICommand PlayRowCommand { get; }
    public ICommand PlayTrackCommand { get; }
    public ICommand StopPlaybackCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelOperationCommand { get; }
    public ICommand BrowseLiveFileCommand { get; }
    public ICommand LiveMirrorSelectedCommand { get; }
    public ICommand StopLiveFileCommand { get; }

    public string LiveFilePath
    {
        get => liveFilePath;
        set { if (Set(ref liveFilePath, value ?? "")) { lastLiveContent = null; SaveLiveSettings(); } }
    }
    public bool LiveFileEnabled
    {
        get => liveFileEnabled;
        set { if (Set(ref liveFileEnabled, value)) SaveLiveSettings(); }
    }
    public string LiveFileStatus { get => liveFileStatus; private set => Set(ref liveFileStatus, value); }

    private string LiveSettingsPath => Path.Combine(controller.Store.RootDirectory, "live-transcript.json");

    private void LoadLiveSettings()
    {
        try
        {
            if (!File.Exists(LiveSettingsPath)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<LiveFileSettings>(File.ReadAllText(LiveSettingsPath));
            if (saved is null) return;
            if (!string.IsNullOrWhiteSpace(saved.Path)) liveFilePath = saved.Path;
            liveFileEnabled = saved.Enabled;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    private void SaveLiveSettings()
    {
        try { File.WriteAllText(LiveSettingsPath, System.Text.Json.JsonSerializer.Serialize(new LiveFileSettings(LiveFilePath, LiveFileEnabled))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void StartLiveFile(Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(LiveFilePath))
        {
            SetStatus("Choose a live transcript file path first.", true);
            return;
        }
        liveSessionId = sessionId;
        lastLiveContent = null;
        LiveFileStatus = $"Live transcript file active → {LiveFilePath.Trim()}";
        CommandManager.InvalidateRequerySuggested();
        UpdateLiveFile();
    }

    private void StopLiveFile()
    {
        liveSessionId = null;
        LiveFileStatus = "Live transcript file stopped. The file was left in place.";
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateLiveFile()
    {
        if (liveSessionId is not { } id || liveWrite is { IsCompleted: false }) return;
        var path = LiveFilePath.Trim();
        if (path.Length == 0) return;
        var previous = lastLiveContent;
        var store = controller.Store;
        liveWrite = Task.Run(() =>
        {
            var content = LiveTranscriptFile.Render(store, id);
            if (content == previous) return (Content: content, Written: false);
            LiveTranscriptFile.Write(path, content);
            return (Content: content, Written: true);
        }).ContinueWith(task => dispatcher.InvokeAsync(() =>
        {
            if (liveSessionId != id || !string.Equals(LiveFilePath.Trim(), path, StringComparison.Ordinal)) return;
            if (task.IsFaulted)
            {
                LiveFileStatus = "Live file update failed; retrying: " + task.Exception?.GetBaseException().Message;
                return;
            }
            lastLiveContent = task.Result.Content;
            if (task.Result.Written) LiveFileStatus = $"Live file updated {DateTime.Now:HH:mm:ss} → {path}";
        }).Task, TaskScheduler.Default).Unwrap();
    }

    private sealed record LiveFileSettings(string Path, bool Enabled);

    public async Task InitializeAsync()
    {
        await Task.Yield();
        RefreshDevices();
        RefreshLibrary();
        ModelStatus = controller.DiarizationModelsReady ? "Local diarization models are installed." : "Local diarization models are not installed.";
        initialized = true;
        refreshTimer.Start();
    }

    private void RefreshDevices()
    {
        var outputId = OutputDevice?.Id;
        var microphoneId = MicrophoneDevice?.Id;
        Replace(OutputDevices, controller.GetOutputDevices());
        Replace(MicrophoneDevices, controller.GetMicrophoneDevices());
        OutputDevice = OutputDevices.FirstOrDefault(x => x.Id == outputId) ?? OutputDevices.FirstOrDefault();
        MicrophoneDevice = MicrophoneDevices.FirstOrDefault(x => x.Id == microphoneId) ?? MicrophoneDevices.FirstOrDefault();
        if (OutputDevices.Count == 0)
            SetStatus("No active output endpoint. Imports still work; connect an output device and refresh.", true);
    }

    private void RefreshLibrary()
    {
        var id = SelectedSession?.Id;
        var sessions = controller.Store.GetSessions(2000);
        if (!Sessions.SequenceEqual(sessions))
        {
            refreshingSelection = true;
            try { Replace(Sessions, sessions); }
            finally { refreshingSelection = false; }
        }
        SelectedSession = sessions.FirstOrDefault(x => x.Id == id) ?? sessions.FirstOrDefault();
        Changed(nameof(SelectedSession));
        RefreshSessionDetails();
        Changed(nameof(IsRecording));
        Changed(nameof(CaptureState));
        Changed(nameof(RecordingSession));
        if (!IsRecording) { OutputLevel = 0; MicrophoneLevel = 0; }
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshSessionDetails()
    {
        if (SelectedSession is not { } session)
        {
            Tracks.Clear(); Speakers.Clear(); SpeakerFilters.Clear(); AssignmentSpeakers.Clear(); Jobs.Clear();
            QueueSummary = "No session selected.";
            return;
        }
        var trackId = SelectedTrack?.Id;
        var tracks = controller.Store.GetTracks(session.Id);
        if (!Tracks.SequenceEqual(tracks))
        {
            Replace(Tracks, tracks);
            SelectedTrack = Tracks.FirstOrDefault(x => x.Id == trackId) ?? Tracks.FirstOrDefault();
        }
        var speakers = controller.Store.GetSpeakers(session.Id);
        if (!Speakers.SequenceEqual(speakers) || SpeakerFilters.Count == 0)
        {
            var filterId = SpeakerFilter?.Id;
            var assignmentId = AssignmentSpeaker?.Id;
            var managedId = ManagedSpeaker?.Id;
            Replace(Speakers, speakers);
            Replace(SpeakerFilters, new[] { new SpeakerChoice(null, "All speakers"), new SpeakerChoice("", "Unknown / unassigned") }
                .Concat(speakers.Select(x => new SpeakerChoice(x.Id, x.Name))));
            Replace(AssignmentSpeakers, new[] { new SpeakerChoice(null, "Unknown / unassigned") }
                .Concat(speakers.Select(x => new SpeakerChoice(x.Id, x.Name))));
            SpeakerFilter = SpeakerFilters.FirstOrDefault(x => x.Id == filterId);
            AssignmentSpeaker = AssignmentSpeakers.FirstOrDefault(x => x.Id == assignmentId);
            ManagedSpeaker = Speakers.FirstOrDefault(x => x.Id == managedId);
        }
        Replace(Jobs, controller.Store.GetJobs(session.Id, 100));
        var progress = controller.Store.GetProgress(session.Id);
        QueueSummary = $"{progress.Pending} queued / retrying   ·   {progress.Running} running   ·   {progress.Succeeded} complete   ·   {progress.Failed} failed / blocked   ·   {progress.Paused} paused / canceled";
        var dirty = SelectedRow is not null && Correction != SelectedRow.Text;
        if (previousSucceeded >= 0 && progress.Succeeded != previousSucceeded && !dirty) LoadPage();
        previousSucceeded = progress.Succeeded;
    }

    private async Task StartRecordingAsync()
    {
        if (SelectedProvider is not { } provider || OutputDevice is not { } output) return;
        if (MicrophoneEnabled && MicrophoneDevice is null)
        {
            SetStatus("Select an available microphone or turn off the separate microphone track.", true);
            return;
        }
        var name = SessionName.Trim();
        var locale = Language.Trim();
        var microphoneId = MicrophoneEnabled ? MicrophoneDevice?.Id : null;
        var consent = provider.IsCloud && NewCloudConsent;
        await RunAsync("Starting the explicitly selected audio devices…", async token =>
        {
            NewCloudConsent = false;
            var session = await controller.StartRecordingAsync(name, output.Id, microphoneId, provider.Id, locale, consent, token);
            if (LiveFileEnabled) StartLiveFile(session.Id);
            RefreshLibrary();
            SelectedSession = Sessions.FirstOrDefault(x => x.Id == session.Id) ?? session;
        });
    }

    private Task StopRecordingAsync()
    {
        stopTask = StopCoreAsync();
        return stopTask;
    }

    private async Task StopCoreAsync()
    {
        Stopping = true;
        SetStatus("Stopping capture and sealing original tails. Transcription is NOT canceled.");
        try
        {
            await controller.StopRecordingAsync();
            SetStatus("Recording stopped. Original audio tails are sealed; transcription may continue.");
            RefreshLibrary();
        }
        catch (Exception error) { Report(error); }
        finally { Stopping = false; Changed(nameof(IsRecording)); Changed(nameof(CaptureState)); }
    }

    private async Task ImportAudioAsync()
    {
        var path = dialogs.OpenAudio();
        if (path is null || SelectedProvider is not { } provider) return;
        var name = SessionName.Trim();
        var locale = Language.Trim();
        var consent = provider.IsCloud && NewCloudConsent;
        await RunAsync("Probing the selected media file…", async token =>
        {
            var probe = await controller.ProbeMediaAsync(path, token);
            if (probe.Streams.Count == 0) throw new InvalidDataException("This file has no audio stream.");
            var stream = probe.Streams.Count == 1 ? probe.Streams[0] : dialogs.ChooseStream(probe);
            if (stream is null) { SetStatus("Import not started."); return; }
            SetStatus($"Importing stream {stream.Index}. Retaining a managed original; preparing audio…");
            NewCloudConsent = false;
            var session = await controller.ImportAudioAsync(name, path, stream.Index, provider.Id, locale, consent, token);
            if (LiveFileEnabled) StartLiveFile(session.Id);
            RefreshLibrary();
            SelectedSession = Sessions.FirstOrDefault(x => x.Id == session.Id) ?? session;
            SetStatus("Audio imported. Jobs and originals are available in the selected session.");
        });
    }

    private Task ImportVttAsync()
    {
        var path = dialogs.OpenVtt();
        return path is null ? Task.CompletedTask :
            RunForSessionAsync("Importing local WebVTT cues…", (id, token) => controller.ImportVttAsync(id, path, token));
    }

    private async Task FetchTeamsAsync()
    {
        var request = dialogs.RequestTeamsTranscript();
        if (request is null) return;
        try
        {
            await RunForSessionAsync("Connecting to configured Microsoft Graph transcript access…",
                (id, token) => controller.FetchTeamsTranscriptAsync(id, request,
                    prompt => dialogs.ShowDeviceSignInAsync(prompt, () => operationCancellation?.Cancel()), token));
        }
        finally { dialogs.CloseDeviceSignIn(); }
    }

    private Task InstallModelsAsync()
    {
        if (!dialogs.Confirm("Install local diarization models?",
            "Download 33,488,994 bytes (about 33.49 MB) of segmentation and speaker-embedding release artifacts?\n\nSegmentation: MIT (CNRS 2023); embedding: CC BY 4.0. Attribution and package notices are retained. These release files need no Hugging Face token; the original Hugging Face segmentation distribution is gated.\n\nNo audio is uploaded. No large Whisper model is included.")) return Task.CompletedTask;
        return RunAsync("Downloading the explicitly requested local diarization models…", async token =>
        {
            await controller.InstallDiarizationModelsAsync(new Progress<string>(message => ModelStatus = message), token);
            ModelStatus = controller.DiarizationModelsReady ? "Local diarization models are installed and ready." : "Model installation did not report ready.";
            CommandManager.InvalidateRequerySuggested();
        });
    }

    private void ChooseWhisperModel()
    {
        var path = dialogs.OpenWhisperModel();
        if (path is null) return;
        Guard(() =>
        {
            controller.SetLocalWhisperModel(path);
            LocalModel = DescribeModel(path);
            SetStatus("Local Whisper model selected. Local-only errors never enable NVIDIA uploads.");
        });
    }

    private static string DescribeModel(string path) =>
        $"{Path.GetFileName(path)} · {new FileInfo(path).Length / (1024d * 1024d):N1} MiB\n{path}";

    private Task InstallWhisperModelAsync()
    {
        var model = AudioTranscriber.Providers.LocalWhisperModelCatalog.Recommended;
        if (!dialogs.Confirm("Install the recommended Whisper model?",
            $"Download {model.FileName} ({model.Bytes:N0} bytes, about {model.Bytes / 1073741824d:N2} GiB) from the pinned whisper.cpp Hugging Face revision?\n\n" +
            "Whisper large-v3-turbo: near large-v3 accuracy at several times the speed. MIT license (OpenAI Whisper; GGML conversion by whisper.cpp contributors). SHA256 is verified before use and the model is selected automatically.\n\nNo audio is uploaded."))
            return Task.CompletedTask;
        return RunAsync("Downloading the recommended local Whisper model…", async token =>
        {
            await controller.InstallRecommendedWhisperModelAsync(new Progress<string>(message => LocalModel = message), token);
            if (controller.WhisperModelPath is { } path) LocalModel = DescribeModel(path);
            SetStatus("Whisper large-v3-turbo installed and selected for local transcription.");
        });
    }

    public void SaveKey(string key) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Enter a NVIDIA API key first.");
        controller.SetNvidiaKey(key, RememberKey);
        Changed(nameof(KeyStatus));
        SetStatus(RememberKey ? "Key remembered using Windows current-user DPAPI." : "Key kept in memory only.");
    });

    private void GrantConsent()
    {
        if (SelectedSession is null || !SelectedCloudConsent) return;
        SessionAction(id => controller.SetCloudConsent(id, true), "Consent granted for this session only. Queued NVIDIA work may upload its audio tracks.");
        SelectedCloudConsent = false;
    }

    private void CancelJobs()
    {
        if (!dialogs.Confirm("Cancel transcription jobs?", "Cancel pending and active transcription jobs for the selected session? Original audio is retained. This is not the recording Stop button.")) return;
        SessionAction(controller.CancelTranscription, "Transcription jobs canceled. Recording, if active, is unaffected.");
    }

    private void SessionAction(Action<Guid> action, string message) => Guard(() =>
    {
        if (SelectedSession is not { } session) return;
        action(session.Id);
        RefreshLibrary();
        SetStatus(message);
    });

    private void ApplySearch() => Guard(() =>
    {
        long? ticks = null;
        if (!string.IsNullOrWhiteSpace(Seek))
        {
            if (!TranscriptPresentation.TryTimestamp(Seek, out var parsed))
                throw new ArgumentException("Use seconds, mm:ss, or hh:mm:ss for the timestamp; hours may exceed 23.");
            ticks = parsed;
        }
        appliedSearch = Search.Trim();
        appliedSpeaker = SpeakerFilter?.Id;
        appliedSeek = ticks;
        ResetPaging();
        LoadPage();
    });

    private void ResetPaging() { cursors.Clear(); cursors.Add(null); pageIndex = 0; }

    private void LoadPage()
    {
        var rowId = SelectedRow?.Row.Id;
        var rows = SelectedSession is { } session
            ? controller.Store.GetTranscriptPage(session.Id, appliedSearch, appliedSpeaker, cursors[pageIndex], TranscriptPresentation.PageSize, appliedSeek)
            : [];
        Replace(Transcript, rows.Select(x => new TranscriptItem(x, Tracks.FirstOrDefault(t => t.Id == x.TrackId)?.Name ?? x.TrackId.ToString())));
        SelectedRow = Transcript.FirstOrDefault(x => x.Row.Id == rowId);
        hasNext = SelectedSession is { } selected && rows.Count == TranscriptPresentation.PageSize &&
            controller.Store.GetTranscriptPage(selected.Id, appliedSearch, appliedSpeaker,
                new TranscriptCursor(rows[^1].StartTicks, rows[^1].Id), 1, appliedSeek).Count != 0;
        Changed(nameof(PageSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    private void NextPage() => Guard(() =>
    {
        if (Transcript.Count == 0) return;
        var last = Transcript[^1].Row;
        var cursor = new TranscriptCursor(last.StartTicks, last.Id);
        if (cursors.Count == pageIndex + 1) cursors.Add(cursor); else cursors[pageIndex + 1] = cursor;
        pageIndex++;
        LoadPage();
    });

    private void SaveCorrection() => Guard(() =>
    {
        if (SelectedRow is not { } row) return;
        controller.Store.CorrectSegment(row.Row.Id, Correction);
        LoadPage();
        SetStatus("Correction saved. Raw recognition and provenance are unchanged.");
    });

    private void RestoreRaw() => Guard(() =>
    {
        if (SelectedRow is not { } row) return;
        controller.Store.CorrectSegment(row.Row.Id, null);
        LoadPage();
        SetStatus("Correction removed; raw recognition is shown again.");
    });

    private void RenameSpeaker() => Guard(() =>
    {
        if (SelectedSession is not { } session || ManagedSpeaker is not { } speaker) return;
        controller.Store.RenameSpeaker(session.Id, speaker.Id, SpeakerName.Trim());
        RefreshSessionDetails();
        LoadPage();
        SetStatus("Speaker display name updated throughout this session. No participant identity was inferred.");
    });

    private void AssignSpeaker() => Guard(() =>
    {
        if (SelectedRow is not { } row || AssignmentSpeaker is not { } speaker) return;
        controller.Store.AssignSpeaker(row.Row.Id, speaker.Id);
        LoadPage();
        SetStatus("Explicit manual speaker assignment saved for this row.");
    });

    private Task PlayRowAsync()
    {
        var row = SelectedRow?.Row;
        return row is null ? Task.CompletedTask : RunAsync("Opening the selected row's original track…",
            token => controller.PlayAsync(row.SessionId, row.TrackId, row.StartTicks, token));
    }

    private Task PlayTrackAsync()
    {
        if (SelectedSession is not { } session || SelectedTrack is not { } track) return Task.CompletedTask;
        if (!TranscriptPresentation.TryTimestamp(PlaybackTimestamp, out var ticks))
        {
            SetStatus("Enter a playback timestamp as seconds, mm:ss, or hh:mm:ss.", true);
            return Task.CompletedTask;
        }
        return RunAsync("Opening the selected original audio track…", token => controller.PlayAsync(session.Id, track.Id, ticks, token));
    }

    private Task ExportAsync()
    {
        if (SelectedSession is not { } session) return Task.CompletedTask;
        var path = dialogs.SaveExport(session.Name);
        return path is null ? Task.CompletedTask : RunAsync("Exporting the complete transcript, one bounded page at a time…",
            token => TranscriptExporter.ExportAsync(controller.Store, session.Id, path, token));
    }

    private Task RunForSessionAsync(string message, Func<Guid, CancellationToken, Task> action)
    {
        if (SelectedSession is not { } session) return Task.CompletedTask;
        return RunAsync(message, async token =>
        {
            await action(session.Id, token);
            RefreshLibrary();
            if (SelectedSession?.Id == session.Id) LoadPage();
        });
    }

    private Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        activeOperation = RunCoreAsync(message, action);
        return activeOperation;
    }

    private async Task RunCoreAsync(string message, Func<CancellationToken, Task> action)
    {
        if (Busy || closing) return;
        Busy = true;
        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        SetStatus(message);
        try
        {
            await action(cancellation.Token);
            if (Status == message) SetStatus("Operation complete.");
        }
        catch (OperationCanceledException) { SetStatus("Operation canceled. Previously retained originals and completed results are not deleted."); }
        catch (Exception error) { Report(error); }
        finally
        {
            operationCancellation = null;
            Busy = false;
            if (!closing) Guard(RefreshLibrary);
        }
    }

    private bool CanWorkWithSession() => SelectedSession is not null && !Busy && !closing;
    private void Guard(Action action) { try { action(); } catch (Exception error) { Report(error); } }
    private void Report(Exception error)
    {
        // Controller notifications carry user-safe service detail; never dump exception bodies or credentials.
        SetStatus(error switch
        {
            ArgumentException => "Check the entered fields, stream, language, and selected model. The operation could not be completed.",
            FileNotFoundException => "A required file or media tool was not found. Check model/audio paths and FFmpeg installation.",
            UnauthorizedAccessException => "Access was denied. Check file permissions, device access, and account configuration.",
            IOException => "A file or media operation failed. Check disk space, paths, and device availability; retained audio is not deleted.",
            _ => "The operation failed. Review the selected session's jobs and error state; no automatic cloud fallback is used."
        }, true);
    }
    private void SetStatus(string message, bool error = false) { Status = message; StatusIsError = error; }
    private void OnNotification(AppNotification notification) => dispatcher.BeginInvoke(() =>
    {
        if (!closing) { SetStatus(notification.Message, notification.IsError); Changed(nameof(IsRecording)); Changed(nameof(CaptureState)); }
    });
    private void OnLevelsChanged(CaptureMeter levels)
    {
        Volatile.Write(ref pendingMeter, levels);
        if (Interlocked.Exchange(ref meterQueued, 1) != 0) return;
        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref meterQueued, 0);
            var current = Volatile.Read(ref pendingMeter);
            if (!closing)
            {
                OutputLevel = double.IsFinite(current.Output) ? Math.Clamp(current.Output, 0, 1) : 0;
                MicrophoneLevel = double.IsFinite(current.Microphone) ? Math.Clamp(current.Microphone, 0, 1) : 0;
            }
        }, DispatcherPriority.Background);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    public async Task ShutdownAsync()
    {
        if (disposed) return;
        closing = true;
        refreshTimer.Stop();
        SetStatus("Closing: waiting for operations and original audio tails to finish…");
        try
        {
            operationCancellation?.Cancel();
            if (activeOperation is not null) await activeOperation;
            if (stopTask is not null) await stopTask;
            if (controller.IsRecording) await controller.StopRecordingAsync();
            if (liveWrite is not null) await liveWrite;
            if (liveSessionId is { } liveId && LiveFilePath.Trim() is { Length: > 0 } livePath)
            {
                try { await Task.Run(() => LiveTranscriptFile.Write(livePath, LiveTranscriptFile.Render(controller.Store, liveId))); }
                catch { }
            }
            controller.StopPlayback();
            await controller.DisposeAsync();
            controller.Notification -= OnNotification;
            controller.LevelsChanged -= OnLevelsChanged;
            dialogs.CloseDeviceSignIn();
            disposed = true;
        }
        catch
        {
            closing = false;
            refreshTimer.Start();
            throw;
        }
    }
}
