using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using AudioTranscriber.Application;
using AudioTranscriber.Core;
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
    private string sessionName = DefaultSessionName();
    private bool sessionNameIsDefault = true;
    private string language = "en";
    private bool microphoneEnabled, newCloudConsent, selectedCloudConsent, reduceEcho = true;
    private bool? savedMicrophoneEnabled;
    private bool microphoneDefaulted, vcInstalling, shownDiarizationReady;
    private string? shownWhisperPath;
    private string setupStatus = "";
    private double outputLevel, microphoneLevel;
    private string search = "", seek = "", appliedSearch = "";
    private SpeakerChoice? speakerFilter;
    private string lineSpeaker = "";
    private bool reloadingTranscript, scrollToTop;
    private IReadOnlyList<TranscriptItem> selectedRows = [];
    private string? appliedSpeaker;
    private long? pendingSeek;
    private TranscriptItem? selectedRow;
    private StoredSpeaker? managedSpeaker;
    private StoredTrack? selectedTrack;
    private string correction = "", speakerName = "", playbackTimestamp = "00:00:00";
    private string queueSummary = "No session selected.";
    private string localModel = "No model selected in this window.";
    private string modelStatus = "";
    private Prerequisites.Report prerequisites = new(null, null, true);
    private readonly AppUpdater updater = new();
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly DispatcherTimer updateTimer;
    private string updateStatus;
    private bool updateBusy;
    private bool rememberKey;
    private int previousSucceeded = -1;
    private CaptureMeter pendingMeter = new(0, 0);
    private int meterQueued;
    private static readonly string LiveFolderRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioTranscriber");
    private static readonly string DefaultLiveFolder = Path.Combine(LiveFolderRoot, "Live transcripts");
    private string liveFilePath = DefaultLiveFolder;
    private bool liveFileEnabled;
    private Guid? liveSessionId;
    private string? liveTargetPath;
    private string? lastLiveContent;
    private Task? liveWrite;
    private bool liveWritePending;
    private string liveFileStatus = "Live transcript file is off.";
    private bool liveFileLoggedWrite, liveFileFailing;
    private Guid? watchedSessionId;
    private long watchedSequence;
    private readonly Dictionary<Guid, string> watchedJobs = new();
    private readonly Dictionary<Guid, string> watchedTracks = new();
    private readonly HashSet<string> watchedNotices = new();
    private string? lastLogText;
    private const int MaxActivityEntries = 1000;
    private const string DiarizationProviderId = "local-diarization";

    public MainViewModel(IAppController controller, DesktopDialogs dialogs, Dispatcher dispatcher)
    {
        this.controller = controller;
        this.dialogs = dialogs;
        this.dispatcher = dispatcher;
        Providers = controller.Providers;
        selectedProvider = Providers.FirstOrDefault(x => !x.IsCloud);
        if (selectedProvider is null)
            selectedProvider = Providers.FirstOrDefault();
        LoadRecordingPreferences();
        RefreshDevicesCommand = new AsyncCommand(() => RunAsync("Enumerating audio endpoints…", _ =>
        {
            RefreshDevices();
            return Task.CompletedTask;
        }), () => !Busy && !IsRecording);
        StartRecordingCommand = new AsyncCommand(StartRecordingAsync, () =>
            !Busy && !IsRecording && !Stopping && OutputDevice is not null && HasNewSessionDetails && !closing);
        ContinueRecordingCommand = new AsyncCommand(ContinueRecordingAsync, () =>
            !Busy && !IsRecording && !Stopping && OutputDevice is not null && SelectedSession is { } s && CanContinueSession(s) && !closing);
        MergeSessionsCommand = new AsyncCommand(MergeSessionsInteractiveAsync, () => Sessions.Count > 1 && !Busy && !closing);
        StopRecordingCommand = new AsyncCommand(StopRecordingAsync, () => IsRecording && !Stopping && !closing);
        ImportAudioCommand = new AsyncCommand(ImportAudioAsync, () => !Busy && HasNewSessionDetails && !closing);
        ImportVttCommand = new AsyncCommand(ImportVttAsync, CanWorkWithSession);
        FetchTeamsCommand = new AsyncCommand(FetchTeamsAsync, CanWorkWithSession);
        InstallModelsCommand = new AsyncCommand(InstallModelsAsync, () => !Busy && !closing && !controller.ModelSetupRunning);
        ChooseWhisperModelCommand = new AsyncCommand(ChooseWhisperModelAsync, () => !Busy && !closing);
        InstallWhisperModelCommand = new AsyncCommand(InstallWhisperModelAsync, () => !Busy && !closing && !controller.ModelSetupRunning);
        InstallParakeetModelCommand = new AsyncCommand(InstallParakeetModelAsync, () => !Busy && !closing && !controller.ModelSetupRunning);
        UseGpuCommand = new AsyncCommand(() => AskGpuAsync(null), () => !closing && controller.GpuParakeetEnabled != true);
        UseCpuCommand = new RelayCommand(() => Guard(() =>
        {
            controller.SetGpuParakeet(false);
            SetStatus("Parakeet uses the CPU. You can switch back to the GPU in Privacy / models.");
        }), () => !closing && controller.GpuParakeetEnabled == true);
        InstallVcRuntimeCommand = new AsyncCommand(InstallVcRuntimeAsync, () => !Busy && !closing && !vcInstalling && !prerequisites.VcRuntimeReady);
        RecheckPrerequisitesCommand = new RelayCommand(() => RefreshPrerequisites(announce: true), () => !closing);
        if (controller.WhisperModelPath is { } currentModel) { localModel = DescribeModel(currentModel); shownWhisperPath = currentModel; }
        DiarizeCommand = new AsyncCommand(() => RunForSessionAsync("Running local speaker analysis…",
            (id, token) => controller.DiarizeSessionAsync(id, token)), () => CanWorkWithSession() && controller.DiarizationModelsReady);
        FillSpeakersCommand = new AsyncCommand(() => RunForSessionAsync("Listening to each line's voice…", async (id, token) =>
        {
            try { await controller.FillSpeakersFromLabelsAsync(id, new Progress<string>(message => SetStatus(message)), token); }
            catch (InvalidOperationException error) { SetStatus(error.Message, true); }
        }), () => CanWorkWithSession() && controller.DiarizationModelsReady);
        ResplitCommand = new AsyncCommand(ResplitAsync, () => CanWorkWithSession() && SelectedSession?.Id != controller.RecordingSessionId);
        NameMicrophoneLinesCommand = new AsyncCommand(NameMicrophoneLinesAsync, CanWorkWithSession);
        PauseCommand = new RelayCommand(() => SessionAction(controller.PauseTranscription,
            "Transcription paused; recording, if active, continues."), () => SelectedSession is not null && !closing);
        ResumeCommand = new RelayCommand(() => SessionAction(controller.ResumeTranscription,
            "Transcription resumed with the session's provider and consent."), () => SelectedSession is not null && !closing);
        CancelJobsCommand = new AsyncCommand(CancelJobsAsync, () => SelectedSession is not null && !closing);
        GrantConsentCommand = new RelayCommand(GrantConsent, () => SelectedSession is not null && SelectedCloudConsent && !closing);
        RevokeConsentCommand = new RelayCommand(() => SessionAction(id => controller.SetCloudConsent(id, false),
            "Cloud consent revoked. Future uploads stop; already sent audio cannot be recalled."), () => SelectedSession is not null && !closing);
        ClearKeyCommand = new RelayCommand(() => Guard(() =>
        {
            controller.ClearNvidiaKey();
            Changed(nameof(KeyStatus));
            SetStatus("NVIDIA key cleared from memory and remembered storage.");
        }));
        RefreshCommand = new RelayCommand(() => Guard(() => { RefreshLibrary(); LoadTranscript(); }), () => !closing);
        DeleteSessionCommand = new AsyncCommand(() => SelectedSession is { } s ? DeleteSessionAsync(s) : Task.CompletedTask,
            () => SelectedSession is { } s && CanDeleteSession(s));
        DeleteSessionsCommand = new AsyncCommand(DeleteSessionsInteractiveAsync, () => Sessions.Count > 0 && !Busy && !closing);
        SearchCommand = new RelayCommand(ApplySearch, () => SelectedSession is not null && !closing);
        ClearSearchCommand = new RelayCommand(() =>
        {
            Search = "";
            Seek = "";
            SpeakerFilter = SpeakerFilters.FirstOrDefault();
            ApplySearch();
        }, () => SelectedSession is not null && !closing);
        SaveCorrectionCommand = new RelayCommand(SaveCorrection, () => SelectedRow is not null && !closing);
        RestoreRawCommand = new RelayCommand(RestoreRaw, () => SelectedRow?.HasCorrection == true && !closing);
        RenameSpeakerCommand = new AsyncCommand(() => RenameSpeakerAsync(ManagedSpeaker, SpeakerName),
            () => SelectedSession is not null && ManagedSpeaker is not null && !string.IsNullOrWhiteSpace(SpeakerName) && !closing);
        SetLineSpeakerCommand = new RelayCommand(SetLineSpeaker, () => SelectedRows.Count > 0 && !closing);
        RecognizeVoicesCommand = new AsyncCommand(RecognizeVoicesAsync, () => CanWorkWithSession() && Voices.Count > 0);
        RememberSessionVoicesCommand = new AsyncCommand(RememberSessionVoicesAsync, CanWorkWithSession);
        RememberAllVoicesCommand = new AsyncCommand(RememberAllVoicesAsync, () => !Busy && !closing);
        RenameVoiceCommand = new AsyncCommand(RenameVoiceAsync, () => SelectedVoice is not null && !closing);
        ForgetVoiceCommand = new AsyncCommand(ForgetVoiceAsync, () => SelectedVoice is not null && !closing);
        ForgetAllVoicesCommand = new AsyncCommand(ForgetAllVoicesAsync, () => Voices.Count > 0 && !closing);
        PlayRowCommand = new AsyncCommand(PlayRowAsync, () => SelectedRow is not null && !Busy && !closing);
        PlayTrackCommand = new AsyncCommand(PlayTrackAsync, () => SelectedSession is not null && SelectedTrack is not null && !Busy && !closing);
        StopPlaybackCommand = new RelayCommand(() => Guard(controller.StopPlayback), () => !closing);
        ExportCommand = new AsyncCommand(ExportAsync, CanWorkWithSession);
        CancelOperationCommand = new RelayCommand(() => operationCancellation?.Cancel(),
            () => Busy && operationCancellation is not null && !closing);
        BrowseLiveFileCommand = new AsyncCommand(async () =>
        {
            if (await dialogs.ChooseLiveFolderAsync(LiveFilePath) is { } path) LiveFilePath = path;
        }, () => !closing);
        LiveMirrorSelectedCommand = new RelayCommand(() => { if (SelectedSession is { } s) StartLiveFile(s.Id); },
            () => SelectedSession is not null && !closing);
        StopLiveFileCommand = new RelayCommand(StopLiveFile, () => liveSessionId is not null && !closing);
        ClearActivityCommand = new RelayCommand(() => { ActivityLog.Clear(); lastLogText = null; });
        CopyActivityCommand = new AsyncCommand(async () =>
        {
            await DesktopDialogs.SetClipboardTextAsync(string.Join(Environment.NewLine, ActivityLog));
        }, () => ActivityLog.Count > 0);
        SaveDiagnosticsCommand = new AsyncCommand(SaveDiagnosticsAsync, () => !closing);
        OpenLogsFolderCommand = new RelayCommand(() => Guard(() =>
            AppDiagnostics.OpenFolder(AppLog.Directory ?? AppDiagnostics.LogDirectory(DataRoot))), () => !closing);
        LoadLiveSettings();
        Templates = new TemplatesViewModel(controller, dialogs, dispatcher, () => controller.RecordingSessionId ?? SelectedSession?.Id,
            (text, error) => Log(text, error ? ActivityKind.Error : ActivityKind.Info));
        controller.LocalLlmExpected = Templates.UsesLocalLlm;
        ApplyRecommendedCommand = new AsyncCommand(ApplyRecommendedAsync, () => !Busy && !closing && Plan is not null);
        RecheckHardwareCommand = new AsyncCommand(async () =>
        {
            HardwareText = "Checking this PC's CPU, memory and graphics cards…";
            await controller.RecheckHardwareAsync();
            recommendationText = "";
            RefreshSetup();
        }, () => !closing);
        Discord = new DiscordViewModel(controller, dispatcher, controller.Store.RootDirectory, RecordFromAsync,
            () => Guard(RefreshDevices), (text, error) => { if (error) SetStatus(text, true); else { SetStatus(text); Log(text); } });
        if (liveFileEnabled) liveFileStatus = ArmedLiveStatus();
        controller.Notification += OnNotification;
        controller.LevelsChanged += OnLevelsChanged;
        controller.TranscriptChanged += OnTranscriptChanged;
        refreshTimer = NewTimer(TimeSpan.FromSeconds(3), (_, _) => { if (initialized && !closing) { Guard(RefreshLibrary); Guard(RefreshSetup); Guard(PollActivity); UpdateLiveFile(); } });
        refreshTimer.Stop();
        updateStatus = updater.IsSupported
            ? updater.StagedTag is { } staged ? $"{staged} is downloaded and installs when you close the app." : "Updates have not been checked yet."
            : "Automatic updates are available only in release ZIP builds.";
        CheckForUpdatesCommand = new AsyncCommand(() => CheckForUpdatesAsync(manual: true), () => updater.IsSupported && !updateBusy && !closing);
        RestartToUpdateCommand = new RelayCommand(() =>
        {
            updater.RelaunchAfterApply = true;
            DesktopDialogs.CloseMainWindow();
        }, () => UpdateReady && !closing);
        updateTimer = NewTimer(TimeSpan.FromHours(6), (_, _) => { if (updater.AutoUpdate && !closing) _ = CheckForUpdatesAsync(manual: false); });
        updateTimer.Stop();
    }

    private static DispatcherTimer NewTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    public ICommand CheckForUpdatesCommand { get; }
    public ICommand SaveDiagnosticsCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }
    public string LogFolderText => $"Log folder: {AppLog.Directory ?? AppDiagnostics.LogDirectory(DataRoot)}";

    private async Task SaveDiagnosticsAsync()
    {
        if (await dialogs.SaveDiagnosticsAsync() is not { } path) return;
        try
        {
            SetStatus("Saving diagnostics ZIP…");
            await AppDiagnostics.CreateBundleAsync(path, controller, ActivityLog.ToArray());
            SetStatus($"Diagnostics saved to {path}. Review it, then attach it to your GitHub issue.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Error("Saving the diagnostics ZIP failed.", error);
            SetStatus("Could not save the diagnostics ZIP: " + error.Message, true);
        }
    }
    public TemplatesViewModel Templates { get; }
    public DiscordViewModel Discord { get; }

    // Selects an external source (a joined Discord channel) as the output and starts a new recording from it.
    private async Task RecordFromAsync(string deviceId)
    {
        RefreshDevices();
        if (OutputDevices.FirstOrDefault(device => device.Id == deviceId) is not { } source) return;
        OutputDevice = source;
        await StartRecordingAsync();
    }

    // Discord already carries everyone's voice, including yours if you're in the call, so no microphone track is added.
    private string? MicrophoneFor(DeviceChoice output) =>
        MicrophoneEnabled && !output.Id.StartsWith(AudioTranscriber.Audio.ExternalAudioSources.Prefix, StringComparison.Ordinal) ? MicrophoneDevice?.Id : null;
    public ICommand RestartToUpdateCommand { get; }
    public string CurrentVersionText => updater.IsSupported
        ? $"Installed version: {updater.CurrentTag} · updates come from the latest GitHub release of {AppUpdater.Repository}"
        : "Development build: automatic updates are disabled (they apply to release ZIP builds only).";
    public string UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }
    public bool UpdateReady => updater.StagedTag is not null;
    public string RestartToUpdateLabel => $"Restart to update ({updater.StagedTag})";
    public bool AutoUpdate
    {
        get => updater.AutoUpdate;
        set
        {
            updater.SetAutoUpdate(value);
            Changed();
            if (value && updater.IsSupported) _ = CheckForUpdatesAsync(manual: false);
        }
    }

    /// <summary>Starts the background update check (release builds only; never in smoke mode).</summary>
    public void StartUpdateChecks()
    {
        if (!updater.IsSupported) return;
        updateTimer.Start();
        if (updater.AutoUpdate) _ = CheckForUpdatesAsync(manual: false);
    }

    /// <summary>Called on application exit: hands a downloaded update to the helper that installs it.</summary>
    public void ApplyPendingUpdate(IReadOnlyList<string> arguments)
    {
        try { if (updater.ApplyOnExit(arguments)) AppLog.Info($"Handing update {updater.StagedTag} to the installer helper."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppLog.Error("Starting the update helper failed.", error); }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (!updater.IsSupported || updateBusy || closing) return;
        updateBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            UpdateStatus = "Checking GitHub for a newer release…";
            var release = await updater.CheckAsync(updateCancellation.Token);
            if (release is null) { UpdateStatus = $"No public release was found. Last checked {DateTime.Now:t}."; return; }
            if (!updater.IsNewer(release)) { UpdateStatus = $"Up to date ({updater.CurrentTag}). Last checked {DateTime.Now:t}."; return; }
            if (updater.StagedTag == release.Tag) { UpdateStatus = $"{release.Tag} is downloaded and installs when you close the app."; return; }
            if (!manual && !updater.AutoUpdate)
            {
                UpdateStatus = $"{release.Tag} is available. Choose Check for updates now to download it.";
                return;
            }
            await updater.DownloadAsync(release, new Progress<string>(message => UpdateStatus = message), updateCancellation.Token);
            UpdateStatus = $"{release.Tag} is downloaded and verified. It installs when you close the app, or choose Restart to update.";
            Changed(nameof(UpdateReady));
            Changed(nameof(RestartToUpdateLabel));
            if (!StatusIsError) SetStatus($"Update {release.Tag} is ready. It installs when you close the app.");
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or IOException or InvalidDataException or
            System.Text.Json.JsonException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or TaskCanceledException)
        {
            AppLog.Warn("Update check failed.", error);
            UpdateStatus = "Update check failed: " + error.Message;
        }
        finally
        {
            updateBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ObservableCollection<StoredSession> Sessions { get; } = [];
    public ObservableCollection<DeviceChoice> OutputDevices { get; } = [];
    public ObservableCollection<DeviceChoice> MicrophoneDevices { get; } = [];
    public ObservableCollection<StoredTrack> Tracks { get; } = [];
    public ObservableCollection<StoredSpeaker> Speakers { get; } = [];
    public ObservableCollection<SpeakerChoice> SpeakerFilters { get; } = [];
    public ObservableCollection<string> SpeakerNames { get; } = [];
    public TranscriptCollection Transcript { get; } = [];
    public ObservableCollection<StoredJob> Jobs { get; } = [];
    // Remembered voices shared by all sessions.
    public ObservableCollection<StoredVoice> Voices { get; } = [];
    public StoredVoice? SelectedVoice { get => selectedVoice; set => Set(ref selectedVoice, value); }
    private StoredVoice? selectedVoice;
    private string voiceStamp = "";
    public string VoiceLibrarySummary => Voices.Count == 0
        ? "No voices remembered yet. Name a speaker in a session (rename, or label their lines) to remember them, or use Learn from past sessions."
        : $"{Voices.Count} remembered voice{(Voices.Count == 1 ? "" : "s")}.";
    public bool RememberVoices
    {
        get => controller.RememberVoices;
        set
        {
            Guard(() => controller.SetRememberVoices(value));
            Changed();
            SetStatus(controller.RememberVoices
                ? "Voices you name are remembered, and later recordings name those speakers automatically."
                : "Voice remembering is off: new names are not remembered and recordings are not matched to remembered voices. Existing voices are kept until you forget them.");
            if (controller.RememberVoices) _ = BackfillVoicesOnceAsync();
        }
    }
    public IReadOnlyList<ProviderOption> Providers { get; }
    public string DataRoot => controller.Store.RootDirectory;
    public bool IsRecording => controller.IsRecording;
    public bool Stopping { get => stopping; private set { Set(ref stopping, value); Changed(nameof(CaptureState)); } }
    public string CaptureState => Stopping ? "STOPPING · sealing audio" :
        IsRecording ? "RECORDING" : "NOT RECORDING";
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
    public string LocalGpuStatus
    {
        get => localGpuStatus;
        private set { if (Set(ref localGpuStatus, value)) Changed(nameof(ProviderHelp)); }
    }
    private string localGpuStatus = OperatingSystem.IsWindows() ? "Checking for a usable NVIDIA GPU after the default models are ready…" : "";
    public string ParakeetStatus { get => parakeetStatus; private set => Set(ref parakeetStatus, value); }
    private string parakeetStatus = "";
    private bool shownParakeetReady, gpuOfferAsked;
    public string HardwareText { get => hardwareText; private set => Set(ref hardwareText, value); }
    private string hardwareText = "Checking this PC's CPU, memory and graphics cards…";
    public string RecommendationText { get => recommendationText; private set => Set(ref recommendationText, value); }
    private string recommendationText = "";
    private AudioTranscriber.Providers.HardwarePlan? Plan => controller.HardwarePlan;
    private AudioTranscriber.Providers.LocalWhisperModel RecommendedWhisper =>
        AudioTranscriber.Providers.LocalWhisperModelCatalog.All.FirstOrDefault(m => m.Id == Plan?.WhisperModelId)
        ?? AudioTranscriber.Providers.LocalWhisperModelCatalog.Recommended;
    public string InstallWhisperLabel => $"Install recommended model ({RecommendedWhisper.Id})…";
    public bool WhisperOnGpu
    {
        get => controller.WhisperOnGpu;
        set => Guard(() => { if (value != controller.WhisperOnGpu) controller.SetWhisperGpu(value); Changed(nameof(WhisperOnGpu)); });
    }
    private bool? shownWhisperGpu;
    public ICommand ApplyRecommendedCommand { get; private set; } = null!;
    public ICommand RecheckHardwareCommand { get; private set; } = null!;
    public string PrerequisiteStatus => prerequisites.Summary;
    public bool PrerequisitesReady => prerequisites.AllReady;
    public bool VcRuntimeMissing => !prerequisites.VcRuntimeReady;
    public string LocalModel { get => localModel; private set => Set(ref localModel, value); }
    public bool RememberKey { get => rememberKey; set => Set(ref rememberKey, value); }
    public string SessionName
    {
        get => sessionName;
        set { if (Set(ref sessionName, value)) sessionNameIsDefault = false; }
    }
    public string SetupStatus { get => setupStatus; private set { if (Set(ref setupStatus, value)) Changed(nameof(HasSetupStatus)); } }
    public bool HasSetupStatus => SetupStatus.Length > 0;
    public string Language { get => language; set => Set(ref language, value); }
    public bool NewCloudConsent { get => newCloudConsent; set => Set(ref newCloudConsent, value); }
    public bool SelectedCloudConsent { get => selectedCloudConsent; set => Set(ref selectedCloudConsent, value); }
    public bool MicrophoneEnabled { get => microphoneEnabled; set => Set(ref microphoneEnabled, value); }
    public bool ReduceEcho { get => reduceEcho; set => Set(ref reduceEcho, value); }
    // Remembered name for microphone lines of new recordings; empty keeps "Me (mic)".
    public string MicrophoneName
    {
        get => microphoneName;
        set { if (Set(ref microphoneName, value ?? "")) SaveRecordingPreferences(); }
    }
    private string microphoneName = "";
    // Silence that ends a live phrase chunk; higher means fewer, longer lines.
    public int PhrasePauseMilliseconds
    {
        get => controller.PhrasePauseMilliseconds;
        set
        {
            var rounded = (int)Math.Round(value / 100.0) * 100;
            if (rounded == controller.PhrasePauseMilliseconds) return;
            controller.PhrasePauseMilliseconds = rounded;
            Changed(nameof(PhrasePauseMilliseconds));
            SaveRecordingPreferences();
        }
    }
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
        ? $"{(p.IsCloud
            ? "Hosted on the internet: audio chunks are uploaded to NVIDIA's cloud. Requires this session's upload consent AND an NVIDIA API key. English only."
            : p.Id == "local-parakeet"
                ? (OperatingSystem.IsWindows() ? "Runs locally on this PC (no upload, no key). Uses the CPU by default, or your NVIDIA GPU if enabled in Privacy / models. Now: " : "Runs locally on this PC (no upload, no key) on the CPU. ") + $"{LocalGpuStatus} Most accurate local option; 25 European languages, detected automatically."
                : "Runs locally on this PC (no upload, no key). Uses your GPU through Vulkan (NVIDIA, AMD, or Intel) when a driver is present, otherwise the CPU (much slower). Any language. English chunks Whisper is unsure about are re-checked by local Parakeet when it's installed, otherwise by hosted Parakeet only if you allow NVIDIA uploads below.")} {p.TimingDescription}."
        : "Choose a transcription provider: Local runs on this PC (CPU or GPU); Internet uploads audio to NVIDIA's cloud.";
    private bool HasNewSessionDetails => !string.IsNullOrWhiteSpace(SessionName) && !string.IsNullOrWhiteSpace(Language) && SelectedProvider is not null;
    public string ContinueRecordingLabel => SelectedSession is { } s
        ? $"●  Continue \"{(s.Name.Length > 28 ? s.Name[..27] + "…" : s.Name)}\"".Replace("_", "__") : "●  Continue selected session";
    public bool CanContinueSession(StoredSession session) =>
        session.State is not ("Starting" or "Recording" or "Stopping" or "Importing") && !IsRecordingSession(session.Id);

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
            Changed(nameof(ContinueRecordingLabel));
            if (!changedId) return;
            SelectedCloudConsent = false;
            previousSucceeded = -1;
            Search = "";
            Seek = "";
            appliedSearch = "";
            appliedSpeaker = null;
            pendingSeek = null;
            scrollToTop = true;
            Guard(() => { RefreshSessionDetails(); LoadTranscript(); });
        }
    }
    public string Search { get => search; set => Set(ref search, value); }
    public string Seek { get => seek; set => Set(ref seek, value); }
    public SpeakerChoice? SpeakerFilter { get => speakerFilter; set => Set(ref speakerFilter, value); }
    // Editable speaker for the selected line(s): pick an existing name or type a new one.
    public string LineSpeaker { get => lineSpeaker; set => Set(ref lineSpeaker, value ?? ""); }
    public StoredTrack? SelectedTrack { get => selectedTrack; set => Set(ref selectedTrack, value); }
    public string PlaybackTimestamp { get => playbackTimestamp; set => Set(ref playbackTimestamp, value); }
    public string Correction { get => correction; set => Set(ref correction, value); }
    public string SpeakerName { get => speakerName; set => Set(ref speakerName, value); }
    public string TranscriptSummary => $"{Transcript.Count:N0} lines · click a line to edit it, right-click to set its speaker";
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
            // Reloading a page briefly empties the grid; keep the selection (and any unsaved edits) across it.
            if ((reloadingTranscript && value is null) || RestoringSelection) return;
            var previous = selectedRow;
            if (!Set(ref selectedRow, value)) return;
            var sameRow = previous is not null && value is not null && previous.Row.Id == value.Row.Id;
            if (!sameRow || Correction == previous!.Text) Correction = value?.Text ?? "";
            if (!sameRow || LineSpeaker == previous!.Speaker) LineSpeaker = value?.Speaker ?? "";
            if (!sameRow) ManagedSpeaker = Speakers.FirstOrDefault(x => x.Id == value?.Row.SpeakerId) ?? ManagedSpeaker;
            if (!reloadingTranscript && (value is null || !selectedRows.Contains(value))) SelectedRows = value is null ? [] : [value];
            Changed(nameof(RowDetails));
            Changed(nameof(RawText));
        }
    }
    public IReadOnlyList<TranscriptItem> SelectedRows
    {
        get => selectedRows;
        private set
        {
            selectedRows = value;
            Changed();
            Changed(nameof(SelectionSummary));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SelectionSummary => SelectedRows.Count > 1 ? $"Speaker ({SelectedRows.Count} lines)" : "Speaker";
    // Raised around a transcript reload. Reloaded passes true when a new session, search or jump replaced the view
    // (the grid should start over) and, for a timestamp jump, the line to bring to the top.
    public event Action? TranscriptReloading;
    public event Action<bool, TranscriptItem?>? TranscriptReloaded;
    // Set by the view while it re-adds a multi-line selection, so the edited line stays the same.
    public bool RestoringSelection { get; set; }
    public void UpdateSelection(IEnumerable<TranscriptItem> rows)
    {
        if (reloadingTranscript) return;
        SelectedRows = rows.OrderBy(row => row.Row.StartTicks).ThenBy(row => row.Row.Id, StringComparer.Ordinal).ToArray();
    }
    // Every session selected in the list (Shift/Ctrl-click); SelectedSession is the one the main pane shows.
    public IReadOnlyList<StoredSession> SelectedSessions { get; private set; } = [];
    // Raised after a library refresh rebuilt the list, with the ids that were selected, so the view can reselect them.
    public event Action<IReadOnlyCollection<Guid>>? SessionsReloaded;
    public void UpdateSessionSelection(IEnumerable<StoredSession> sessions)
    {
        if (refreshingSelection) return;
        SelectedSessions = sessions.ToArray();
        CommandManager.InvalidateRequerySuggested();
    }
    public string RawText => SelectedRow?.RawText ?? "";
    public string RowDetails => SelectedRow is { } item
        ? $"{item.TrackName}  ·  {item.Timestamp} – {TranscriptPresentation.Duration(item.EndTicks)}  ·  {item.Timing}  ·  {item.Attribution}" +
          (item.Rows.Count > 1 ? $"  ·  {item.Rows.Count} same-speaker segments merged" : "") + $"\nSource: {item.Provenance}"
        : "Select a row to inspect raw recognition, correct text, or play its own audio track.";

    public ICommand RefreshDevicesCommand { get; }
    public ICommand StartRecordingCommand { get; }
    public ICommand ContinueRecordingCommand { get; }
    public ICommand MergeSessionsCommand { get; }
    public ICommand StopRecordingCommand { get; }
    public ICommand ImportAudioCommand { get; }
    public ICommand ImportVttCommand { get; }
    public ICommand FetchTeamsCommand { get; }
    public ICommand InstallModelsCommand { get; }
    public ICommand ChooseWhisperModelCommand { get; }
    public ICommand InstallWhisperModelCommand { get; }
    public ICommand InstallParakeetModelCommand { get; }
    public ICommand UseGpuCommand { get; }
    public ICommand UseCpuCommand { get; }
    public ICommand InstallVcRuntimeCommand { get; }
    public ICommand RecheckPrerequisitesCommand { get; }
    public ICommand DiarizeCommand { get; }
    public ICommand FillSpeakersCommand { get; }
    public ICommand ResplitCommand { get; }
    public ICommand NameMicrophoneLinesCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelJobsCommand { get; }
    public ICommand GrantConsentCommand { get; }
    public ICommand RevokeConsentCommand { get; }
    public ICommand ClearKeyCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand DeleteSessionCommand { get; }
    public ICommand DeleteSessionsCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand SaveCorrectionCommand { get; }
    public ICommand RestoreRawCommand { get; }
    public ICommand RenameSpeakerCommand { get; }
    public ICommand SetLineSpeakerCommand { get; }
    public ICommand RecognizeVoicesCommand { get; }
    public ICommand RememberSessionVoicesCommand { get; }
    public ICommand RememberAllVoicesCommand { get; }
    public ICommand RenameVoiceCommand { get; }
    public ICommand ForgetVoiceCommand { get; }
    public ICommand ForgetAllVoicesCommand { get; }
    public ICommand PlayRowCommand { get; }
    public ICommand PlayTrackCommand { get; }
    public ICommand StopPlaybackCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelOperationCommand { get; }
    public ICommand BrowseLiveFileCommand { get; }
    public ICommand LiveMirrorSelectedCommand { get; }
    public ICommand StopLiveFileCommand { get; }
    public ICommand ClearActivityCommand { get; }
    public ICommand CopyActivityCommand { get; }
    public ObservableCollection<ActivityEntry> ActivityLog { get; } = [];

    public string LiveFilePath
    {
        get => liveFilePath;
        set
        {
            if (!Set(ref liveFilePath, value ?? "")) return;
            lastLiveContent = null;
            liveFileLoggedWrite = false;
            SaveLiveSettings();
            if (liveSessionId is null && LiveFileEnabled) LiveFileStatus = ArmedLiveStatus();
            else if (liveSessionId is { } id)
            {
                liveTargetPath = LiveTargetFor(id);
                UpdateLiveFile();
            }
        }
    }
    public bool LiveFileEnabled
    {
        get => liveFileEnabled;
        set
        {
            if (!Set(ref liveFileEnabled, value)) return;
            SaveLiveSettings();
            // Ticking the box during a recording mirrors that recording right away instead of waiting for the next session.
            if (value && liveSessionId is null && controller.RecordingSessionId is { } recording) StartLiveFile(recording);
            else if (!value && liveSessionId is not null) StopLiveFile();
            else if (liveSessionId is null)
            {
                LiveFileStatus = value ? ArmedLiveStatus() : "Live transcript file is off.";
                Log(value ? LiveFileStatus : "Live transcript file turned off.");
            }
        }
    }
    public string LiveFileStatus { get => liveFileStatus; private set => Set(ref liveFileStatus, value); }

    private string ArmedLiveStatus()
    {
        if (string.IsNullOrWhiteSpace(LiveFilePath)) return "Live file is on, but no folder or file path is set.";
        var target = LiveTranscriptFile.IsFolder(LiveFilePath)
            ? Path.Combine(LiveFilePath.Trim(), "<session name>.txt") : LiveFilePath.Trim();
        return $"Live file is on: the next recording or import is written to {target}";
    }

    private string? LiveTargetFor(Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(LiveFilePath)) return null;
        var session = Sessions.FirstOrDefault(x => x.Id == sessionId) ?? controller.Store.GetSession(sessionId);
        return LiveTranscriptFile.TargetPath(LiveFilePath, session);
    }

    private string LiveSettingsPath => Path.Combine(controller.Store.RootDirectory, "live-transcript.json");

    private void LoadLiveSettings()
    {
        try
        {
            if (!File.Exists(LiveSettingsPath)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<LiveFileSettings>(File.ReadAllText(LiveSettingsPath));
            if (saved is null) return;
            // The old default was one shared file; move it to the per-session folder. A file the user picked stays fixed.
            if (!string.IsNullOrWhiteSpace(saved.Path))
                liveFilePath = string.Equals(saved.Path, Path.Combine(LiveFolderRoot, "live-transcript.txt"), StringComparison.OrdinalIgnoreCase)
                    ? DefaultLiveFolder : saved.Path;
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
        if (LiveTargetFor(sessionId) is not { } target)
        {
            SetStatus("Choose a live transcript folder or file path first.", true);
            return;
        }
        liveSessionId = sessionId;
        liveTargetPath = target;
        lastLiveContent = null;
        liveFileLoggedWrite = false;
        liveFileFailing = false;
        var name = Sessions.FirstOrDefault(x => x.Id == sessionId)?.Name ?? controller.Store.GetSession(sessionId).Name;
        LiveFileStatus = $"Live transcript file active → {target}";
        Log($"Live file on: mirroring \"{name}\" → {target}");
        CommandManager.InvalidateRequerySuggested();
        UpdateLiveFile();
    }

    private void StopLiveFile()
    {
        liveSessionId = null;
        LiveFileStatus = "Live transcript file stopped. The file was left in place.";
        Log(LiveFileStatus);
        CommandManager.InvalidateRequerySuggested();
    }

    // Mirror new transcript text to the live file as soon as a chunk is recognized, not on the next timer tick.
    private void OnTranscriptChanged(Guid sessionId) => dispatcher.Post(() =>
    {
        if (!closing && liveSessionId == sessionId) UpdateLiveFile();
    });

    private void UpdateLiveFile()
    {
        if (liveSessionId is not { } id) return;
        if (liveWrite is { IsCompleted: false })
        {
            liveWritePending = true;
            return;
        }
        liveWritePending = false;
        if (liveTargetPath is not { Length: > 0 } path) return;
        var previous = lastLiveContent;
        var store = controller.Store;
        liveWrite = Task.Run(() =>
        {
            var content = LiveTranscriptFile.Render(store, id, out var rows);
            if (content == previous) return (Content: content, Rows: rows, Bytes: -1L);
            return (Content: content, Rows: rows, Bytes: LiveTranscriptFile.Write(path, content));
        }).ContinueWith(task => dispatcher.Post(() =>
        {
            if (liveSessionId != id || !string.Equals(liveTargetPath, path, StringComparison.Ordinal)) return;
            if (task.IsFaulted)
            {
                LiveFileStatus = "Live file update failed; retrying: " + task.Exception?.GetBaseException().Message;
                if (!liveFileFailing) Log(LiveFileStatus, ActivityKind.Error);
                liveFileFailing = true;
                return;
            }
            var (content, rows, bytes) = task.Result;
            lastLiveContent = content;
            if (bytes < 0) return;
            var lines = rows == 1 ? "1 transcript line" : $"{rows:N0} transcript lines";
            LiveFileStatus = $"Live file updated {DateTime.Now:HH:mm:ss} · {lines} · {bytes:N0} bytes → {path}";
            if (!liveFileLoggedWrite || liveFileFailing)
                Log($"Live file written: {lines}, {bytes:N0} bytes on disk → {path}");
            else if (rows > 0)
                Log($"Live file updated: {lines} → {Path.GetFileName(path)}");
            liveFileLoggedWrite = true;
            liveFileFailing = false;
        }), TaskScheduler.Default);
        liveWrite.ContinueWith(_ => dispatcher.Post(() =>
        {
            if (liveWritePending && !closing) UpdateLiveFile();
        }), TaskScheduler.Default);
    }

    private void Log(string text, ActivityKind kind = ActivityKind.Info, string? group = null, bool persist = true)
    {
        if (!dispatcher.CheckAccess()) { dispatcher.Post(() => Log(text, kind, group, persist)); return; }
        if (closing || string.IsNullOrWhiteSpace(text)) return;
        if (kind != ActivityKind.Transcript && text == lastLogText) return;
        lastLogText = text;
        // Transcript lines stay out of the diagnostic log; progress groups would only repeat themselves there.
        if (persist && kind != ActivityKind.Transcript && group is null)
            AppLog.Write(kind == ActivityKind.Error ? LogLevel.Error : LogLevel.Info, text, null);
        var entry = new ActivityEntry(DateTime.Now, kind, text, group);
        // Consecutive progress updates of the same kind (copying, audio ready through …) replace each other.
        if (group is not null && ActivityLog.Count > 0 && ActivityLog[^1].Group == group) ActivityLog[^1] = entry;
        else ActivityLog.Add(entry);
        while (ActivityLog.Count > MaxActivityEntries) ActivityLog.RemoveAt(0);
    }

    private void LogNotification(string message)
    {
        for (var i = ActivityLog.Count - 1; i >= Math.Max(0, ActivityLog.Count - 50); i--)
            if (ActivityLog[i].Text == message) return;
        var digit = message.AsSpan().IndexOfAnyInRange('0', '9');
        Log(message, ActivityKind.Info, digit >= 8 ? message[..digit] : null, persist: false);
    }

    // Follows the recording, mirrored, or selected session and logs job progress plus each new transcript line.
    private void PollActivity()
    {
        var target = controller.RecordingSessionId ?? liveSessionId ?? SelectedSession?.Id;
        if (target is not { } id) return;
        var store = controller.Store;
        if (watchedSessionId != id)
        {
            watchedSessionId = id;
            watchedSequence = store.GetLatestSegmentSequence(id);
            watchedJobs.Clear();
            watchedTracks.Clear();
            watchedNotices.Clear();
            foreach (var job in store.GetJobs(id, 300)) watchedJobs[job.Id] = JobKey(job);
            var existing = store.CountSegments(id);
            var name = Sessions.FirstOrDefault(x => x.Id == id)?.Name ?? store.GetSession(id).Name;
            Log($"Following \"{name}\" ({(existing == 0 ? "no transcript lines yet" : $"{existing:N0} transcript lines so far")}). New lines appear here as they are transcribed.");
            return;
        }
        foreach (var job in store.GetJobs(id, 300).Reverse())
        {
            var key = JobKey(job);
            var known = watchedJobs.TryGetValue(job.Id, out var previous);
            if (known && previous == key) continue;
            watchedJobs[job.Id] = key;
            LogJob(job, known);
        }
        var latest = store.GetLatestSegmentSequence(id);
        if (latest <= watchedSequence) return;
        const int limit = 100;
        var rows = store.GetSegmentsAddedAfter(id, watchedSequence, limit);
        foreach (var (sequence, row) in rows)
        {
            watchedSequence = sequence;
            Log($"{TrackName(row.TrackId)} [{row.Timestamp}] {row.SpeakerName}: {row.Text.Trim()}", ActivityKind.Transcript);
        }
        if (latest > watchedSequence)
        {
            Log("…more lines were added at once; see the Transcript tab for all of them.");
            watchedSequence = latest;
        }
    }

    private static string JobKey(StoredJob job) => job.State + "|" + job.Error;

    private void LogJob(StoredJob job, bool known)
    {
        var diarization = job.ProviderId == DiarizationProviderId;
        var what = diarization ? "Speaker analysis" : "Transcription";
        switch (job.State)
        {
            case "Running" when !diarization:
                Log($"Transcribing {ChunkLabel(job)} with {job.ProviderId}…");
                break;
            case "Succeeded" when !diarization:
                var count = controller.Store.CountJobSegments(job.Id);
                Log($"Transcribed {ChunkLabel(job)}: " + (count == 0 ? "no speech detected." : count == 1 ? "1 line." : $"{count} lines."));
                break;
            case "Failed" or "Blocked" or "RetryWaiting" when diarization:
                if (watchedNotices.Add(job.State + job.Error))
                    Log($"Speaker analysis {(job.State == "RetryWaiting" ? "will retry" : job.State.ToLowerInvariant())}: {job.Error ?? "no detail"}", ActivityKind.Error);
                break;
            case "Failed" or "Blocked":
                Log($"{what} {job.State.ToLowerInvariant()} for {ChunkLabel(job)}: {job.Error ?? "no detail"}", ActivityKind.Error);
                break;
            case "RetryWaiting":
                Log($"{what} will retry for {ChunkLabel(job)}: {job.Error ?? "no detail"}", ActivityKind.Error);
                break;
            case "Pending" when job.Error is { Length: > 0 } reason:
                if (watchedNotices.Add(reason)) Log($"{what} waiting: {reason}");
                break;
            case "Paused" or "Canceled" when known && !diarization:
                Log($"Transcription {job.State.ToLowerInvariant()} for {ChunkLabel(job)}.");
                break;
        }
    }

    private string ChunkLabel(StoredJob job)
    {
        try
        {
            var chunk = controller.Store.GetChunk(job.ChunkId);
            var end = chunk.StartTicks + chunk.SampleCount * TimeSpan.TicksPerSecond / 16000L;
            return $"{TrackName(job.TrackId)} {TranscriptPresentation.Duration(chunk.StartTicks)}–{TranscriptPresentation.Duration(end)}";
        }
        catch (InvalidOperationException) { return TrackName(job.TrackId); }
    }

    private string TrackName(Guid trackId)
    {
        if (watchedTracks.TryGetValue(trackId, out var name)) return name;
        if (watchedSessionId is { } session)
            foreach (var track in controller.Store.GetTracks(session)) watchedTracks[track.Id] = track.Name;
        return watchedTracks.TryGetValue(trackId, out name) ? name : "Track";
    }

    private sealed record LiveFileSettings(string Path, bool Enabled);

    private sealed record RecordingPreferences(bool? MicrophoneEnabled, string? ProviderId, string? Language, bool? ReduceEcho = null,
        int? Version = null, string? MicrophoneName = null, int? PhrasePauseMilliseconds = null);

    private string RecordingPreferencesPath => Path.Combine(controller.Store.RootDirectory, "recording-defaults.json");

    private void LoadRecordingPreferences()
    {
        try
        {
            if (!File.Exists(RecordingPreferencesPath)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<RecordingPreferences>(File.ReadAllText(RecordingPreferencesPath));
            if (saved is null) return;
            // Whisper was the only local choice before version 2; move those saved defaults to Parakeet once.
            var migrated = saved.Version is null && saved.ProviderId == "local-whisper" &&
                AudioTranscriber.Diarization.SherpaParakeetProvider.SupportsLanguage(string.IsNullOrWhiteSpace(saved.Language) ? "en" : saved.Language);
            if (!migrated && Providers.FirstOrDefault(x => x.Id == saved.ProviderId) is { } provider) selectedProvider = provider;
            if (!string.IsNullOrWhiteSpace(saved.Language)) language = saved.Language;
            savedMicrophoneEnabled = saved.MicrophoneEnabled;
            reduceEcho = saved.ReduceEcho ?? true;
            microphoneName = saved.MicrophoneName?.Trim() ?? "";
            if (saved.PhrasePauseMilliseconds is { } pause) controller.PhrasePauseMilliseconds = pause;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    private void SaveRecordingPreferences()
    {
        try
        {
            File.WriteAllText(RecordingPreferencesPath, System.Text.Json.JsonSerializer.Serialize(
                new RecordingPreferences(MicrophoneEnabled, SelectedProvider?.Id, Language.Trim(), ReduceEcho, Version: 2,
                    MicrophoneName: MicrophoneName.Trim(), PhrasePauseMilliseconds: PhrasePauseMilliseconds)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string DefaultSessionName() => $"Session {DateTime.Now:yyyy-MM-dd HH:mm}";

    private string TakeSessionName()
    {
        if (sessionNameIsDefault)
        {
            sessionName = DefaultSessionName();
            Changed(nameof(SessionName));
        }
        return SessionName.Trim();
    }

    private void ResetSessionName()
    {
        sessionName = DefaultSessionName();
        sessionNameIsDefault = true;
        Changed(nameof(SessionName));
    }

    private void RefreshSetup()
    {
        SetupStatus = controller.SetupStatus ?? "";
        if (controller.LocalGpuStatus is { } gpu) LocalGpuStatus = gpu;
        if (controller.GpuOffer is { } offer && !gpuOfferAsked && !closing)
        {
            // Asked once; the answer is saved, and Privacy / models can change it later.
            gpuOfferAsked = true;
            dispatcher.Post(() => _ = AskGpuAsync(offer));
        }
        if (controller.ParakeetModelReady != shownParakeetReady || ParakeetStatus.Length == 0)
        {
            shownParakeetReady = controller.ParakeetModelReady;
            ParakeetStatus = ParakeetReadyText;
        }
        if (controller.WhisperModelPath is { } path && path != shownWhisperPath)
        {
            shownWhisperPath = path;
            LocalModel = DescribeModel(path);
        }
        var ready = controller.DiarizationModelsReady;
        if (ready != shownDiarizationReady)
        {
            shownDiarizationReady = ready;
            ModelStatus = ready ? "Local diarization models are installed." : "Local diarization models are not installed.";
        }
        RefreshHardwarePlan();
    }

    private void RefreshHardwarePlan()
    {
        controller.LocalLlmExpected = Templates.UsesLocalLlm;
        if (controller.WhisperOnGpu != shownWhisperGpu)
        {
            shownWhisperGpu = controller.WhisperOnGpu;
            Changed(nameof(WhisperOnGpu));
        }
        if (Plan is not { } plan) return;
        if (Templates.TakeFreshDefaults() && plan.LlmModel is { } model && Templates.UseRecommendedLlm(model).Count > 0)
            Log($"Templates use {model} in Ollama: the recommended size for this PC ({plan.LlmDevice switch { AudioTranscriber.Providers.PlanDevice.Gpu => "fits in GPU memory", _ => "runs on the CPU" }}).");
        var text = plan.Summary;
        if (text == RecommendationText) return;
        HardwareText = "Detected: " + plan.Hardware.Describe() + ".";
        RecommendationText = text;
        Changed(nameof(InstallWhisperLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Applies the hardware plan: Whisper size and device, Parakeet device, and the local template model.</summary>
    private async Task ApplyRecommendedAsync()
    {
        if (Plan is not { } plan) return;
        if (!await dialogs.ConfirmAsync("Apply the recommended setup for this PC?",
                HardwareText + "\n\n" + plan.Summary + "\n\nGPU downloads still ask first. You can change each setting afterward."))
            return;
        var done = new List<string>();
        Guard(() =>
        {
            controller.SetWhisperGpu(plan.WhisperOnGpu);
            done.Add($"Whisper on the {(plan.WhisperOnGpu ? "GPU" : "CPU")}");
            Changed(nameof(WhisperOnGpu));
            if (plan.LlmModel is { } model && Templates.UseRecommendedLlm(model) is { Count: > 0 } changed)
                done.Add($"{string.Join(", ", changed)} uses {model}");
            if (!plan.ParakeetOnGpu && controller.GpuParakeetEnabled != false)
            {
                controller.SetGpuParakeet(false);
                LocalGpuStatus = controller.LocalGpuStatus ?? LocalGpuStatus;
                done.Add("Parakeet on the CPU");
            }
        });
        if (plan.ParakeetOnGpu && controller.GpuParakeetEnabled != true) await AskGpuAsync(plan.Gpu?.Name);
        var whisper = RecommendedWhisper;
        var whisperSelected = controller.SelectInstalledWhisperModel(whisper.Id);
        if (whisperSelected)
        {
            LocalModel = DescribeModel(controller.WhisperModelPath!);
            done.Add($"Whisper {whisper.Id}");
        }
        SetStatus("Recommended setup applied: " + string.Join("; ", done) + ".");
        // Download a different Whisper size only when one is already installed; otherwise it downloads when first needed.
        if (!whisperSelected && controller.WhisperModelPath is not null &&
            await dialogs.ConfirmAsync("Download the recommended Whisper model?",
                $"Whisper {whisper.Id} suits this PC better than the installed model. Download it now ({whisper.Bytes / 1048576d:N0} MiB, SHA256-verified, MIT)?"))
            await RunAsync($"Downloading Whisper {whisper.Id}…", async token =>
            {
                await controller.InstallWhisperModelAsync(whisper.Id, new Progress<string>(message => LocalModel = message), token);
                if (controller.WhisperModelPath is { } path) LocalModel = DescribeModel(path);
                SetStatus($"Whisper {whisper.Id} installed and selected.");
            });
    }

    public async Task InitializeAsync()
    {
        await Task.Yield();
        RefreshDevices();
        RefreshLibrary();
        ModelStatus = controller.DiarizationModelsReady ? "Local diarization models are installed." : "Local diarization models are not installed.";
        shownDiarizationReady = controller.DiarizationModelsReady;
        RefreshPrerequisites(announce: false);
        initialized = true;
        refreshTimer.Start();
        Templates.Start();
    }

    private void RefreshPrerequisites(bool announce)
    {
        prerequisites = Prerequisites.Check();
        Changed(nameof(PrerequisiteStatus));
        Changed(nameof(PrerequisitesReady));
        Changed(nameof(VcRuntimeMissing));
        CommandManager.InvalidateRequerySuggested();
        if (!prerequisites.MediaToolsReady)
            SetStatus("FFmpeg was not found, so recording normalization and imports would pause. " +
                MediaToolLocatorMessage() + " See Privacy / models → Prerequisites.", true);
        else if (!prerequisites.VcRuntimeReady)
            SetStatus("The Microsoft Visual C++ runtime is missing or outdated, so local Whisper cannot run. Install it from Privacy / models → Prerequisites.", true);
        else if (announce) SetStatus("All prerequisites are ready: FFmpeg, FFprobe, and the Visual C++ runtime were found.");
    }

    private string MediaToolLocatorMessage() =>
        AudioTranscriber.Audio.MediaToolLocator.MissingMessage(prerequisites.FFmpeg is null ? "ffmpeg" : "ffprobe");

    /// <summary>
    /// Called once after startup (never in smoke mode) so Start recording works with no configuration:
    /// downloads the default Whisper and speaker models and installs a missing Visual C++ runtime.
    /// </summary>
    public Task RunAutomaticSetupAsync()
    {
        if (closing) return Task.CompletedTask;
        _ = controller.EnsureDefaultModelsAsync();
        _ = BackfillVoicesOnceAsync();
        Guard(RefreshSetup);
        CommandManager.InvalidateRequerySuggested();
        return prerequisites.VcRuntimeReady ? Task.CompletedTask : AutoInstallVcRuntimeAsync();
    }

    private async Task AutoInstallVcRuntimeAsync()
    {
        vcInstalling = true;
        CommandManager.InvalidateRequerySuggested();
        string result;
        try
        {
            result = await Prerequisites.InstallVcRuntimeAsync(new Progress<string>(message =>
            {
                if (!closing) SetStatus("Setting up local transcription: " + message);
            }), CancellationToken.None);
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or TaskCanceledException)
        { result = "The Visual C++ runtime could not be installed automatically: " + error.Message + " Install it from Privacy / models → Prerequisites."; }
        finally { vcInstalling = false; }
        if (closing) return;
        RefreshPrerequisites(announce: false);
        SetStatus(result, !prerequisites.VcRuntimeReady);
        if (prerequisites.VcRuntimeReady) Guard(controller.RetryBlockedLocalWork);
    }

    private async Task InstallVcRuntimeAsync()
    {
        if (!await dialogs.ConfirmAsync("Install the Microsoft Visual C++ runtime?",
            "Download Microsoft's official Visual C++ 2015-2022 runtime installer (x64, about 25 MB) and run it now? Windows will ask for administrator approval."))
            return;
        await InstallVcRuntimeCoreAsync();
    }

    private Task InstallVcRuntimeCoreAsync() => RunAsync("Installing the Microsoft Visual C++ runtime…", async token =>
    {
        string result;
        try { result = await Prerequisites.InstallVcRuntimeAsync(new Progress<string>(message => SetStatus(message)), token); }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { result = "The Visual C++ runtime could not be installed automatically: " + error.Message; }
        RefreshPrerequisites(announce: false);
        SetStatus(result, !prerequisites.VcRuntimeReady);
        if (prerequisites.VcRuntimeReady) controller.RetryBlockedLocalWork();
    });

    private void RefreshDevices()
    {
        var outputId = OutputDevice?.Id;
        var microphoneId = MicrophoneDevice?.Id;
        Replace(OutputDevices, controller.GetOutputDevices());
        Replace(MicrophoneDevices, controller.GetMicrophoneDevices());
        OutputDevice = OutputDevices.FirstOrDefault(x => x.Id == outputId) ?? OutputDevices.FirstOrDefault();
        MicrophoneDevice = MicrophoneDevices.FirstOrDefault(x => x.Id == microphoneId) ?? MicrophoneDevices.FirstOrDefault();
        if (!microphoneDefaulted)
        {
            // Capture your own voice by default when a microphone exists; a saved choice wins.
            microphoneDefaulted = true;
            MicrophoneEnabled = (savedMicrophoneEnabled ?? true) && MicrophoneDevices.Count > 0;
        }
        if (OutputDevices.Count == 0)
            SetStatus("No active output endpoint. Imports still work; connect an output device and refresh.", true);
    }

    private void RefreshLibrary()
    {
        var id = SelectedSession?.Id;
        var keep = SelectedSessions.Select(session => session.Id).ToHashSet();
        var sessions = controller.Store.GetSessions(2000);
        var reloaded = false;
        if (!Sessions.SequenceEqual(sessions))
        {
            refreshingSelection = true;
            try { Replace(Sessions, sessions); }
            finally { refreshingSelection = false; }
            reloaded = true;
        }
        SelectedSession = sessions.FirstOrDefault(x => x.Id == id) ?? sessions.FirstOrDefault();
        Changed(nameof(SelectedSession));
        if (reloaded && keep.Count > 1) SessionsReloaded?.Invoke(keep);
        RefreshSessionDetails();
        RefreshVoices();
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
            Tracks.Clear(); Speakers.Clear(); SpeakerFilters.Clear(); SpeakerNames.Clear(); Jobs.Clear();
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
            var managedId = ManagedSpeaker?.Id;
            var typedSpeaker = LineSpeaker;
            var typedName = SpeakerName;
            Replace(Speakers, speakers);
            Replace(SpeakerFilters, new[] { new SpeakerChoice(null, "All speakers"), new SpeakerChoice("", "Unknown / unassigned") }
                .Concat(speakers.Select(x => new SpeakerChoice(x.Id, x.Name))));
            Replace(SpeakerNames, speakers.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase).Append(UnknownSpeaker));
            SpeakerFilter = SpeakerFilters.FirstOrDefault(x => x.Id == filterId);
            ManagedSpeaker = Speakers.FirstOrDefault(x => x.Id == managedId);
            // Replacing an editable combo's items can clear its text; keep what the user typed.
            LineSpeaker = typedSpeaker;
            if (ManagedSpeaker?.Id == managedId) SpeakerName = typedName;
        }
        Replace(Jobs, controller.Store.GetJobs(session.Id, 100));
        var progress = controller.Store.GetProgress(session.Id);
        QueueSummary = $"{progress.Pending} queued / retrying   ·   {progress.Running} running   ·   {progress.Succeeded} complete   ·   {progress.Failed} failed / blocked   ·   {progress.Paused} paused / canceled";
        var dirty = SelectedRow is not null && Correction != SelectedRow.Text;
        if (previousSucceeded >= 0 && progress.Succeeded != previousSucceeded && !dirty) LoadTranscript();
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
        var name = TakeSessionName();
        var locale = Language.Trim();
        var microphoneId = MicrophoneFor(output);
        var consent = NewCloudConsent;
        var reduceEcho = ReduceEcho;
        SaveRecordingPreferences();
        await RunAsync("Starting the explicitly selected audio devices…", async token =>
        {
            NewCloudConsent = false;
            var session = await controller.StartRecordingAsync(name, output.Id, microphoneId, provider.Id, locale, consent, reduceEcho, token);
            ApplyMicrophoneName(session, microphoneId);
            if (sessionNameIsDefault) ResetSessionName();
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

    // Records into the selected session; the live file keeps mirroring that same session, so nothing is overwritten.
    private async Task ContinueRecordingAsync()
    {
        if (SelectedSession is not { } target || OutputDevice is not { } output) return;
        if (MicrophoneEnabled && MicrophoneDevice is null)
        {
            SetStatus("Select an available microphone or turn off the separate microphone track.", true);
            return;
        }
        var microphoneId = MicrophoneFor(output);
        var reduceEcho = ReduceEcho;
        SaveRecordingPreferences();
        await RunAsync($"Continuing \"{target.Name}\" with the selected audio devices…", async token =>
        {
            StoredSession session;
            try { session = await controller.ContinueRecordingAsync(target.Id, output.Id, microphoneId, reduceEcho, token); }
            catch (InvalidOperationException error) { SetStatus(error.Message, true); return; }
            ApplyMicrophoneName(session, microphoneId);
            if (LiveFileEnabled && liveSessionId != session.Id) StartLiveFile(session.Id);
            RefreshLibrary();
            SelectedSession = Sessions.FirstOrDefault(x => x.Id == session.Id) ?? session;
            SetStatus($"Recording continues in \"{session.Name}\"; new audio and text follow what it already had.");
        });
    }

    // A continued session keeps the microphone name it already has.
    private void ApplyMicrophoneName(StoredSession session, string? microphoneId)
    {
        if (microphoneId is null || MicrophoneName.Trim().Length == 0 || session.MicrophoneSpeakerId is not null) return;
        try { controller.SetMicrophoneSpeaker(session.Id, MicrophoneName.Trim()); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { SetStatus(error.Message, true); }
    }

    public async Task NameMicrophoneLinesAsync()
    {
        if (SelectedSession is not { } session) return;
        if (!controller.Store.GetTracks(session.Id).Any(track => track.Kind == "Microphone"))
        {
            SetStatus("This session has no microphone track, so there are no microphone lines to name.", true);
            return;
        }
        var current = session.MicrophoneSpeakerId is { } id ? Speakers.FirstOrDefault(x => x.Id == id)?.Name : null;
        var name = await dialogs.PromptSpeakerNameAsync("Name all microphone lines",
            "Every line on this session's microphone track gets this speaker, and so does every microphone line transcribed later " +
            "(including when you continue this recording). Pick an existing name or type a new one." +
            (MicrophoneName.Trim().Length == 0 ? " New recordings will use it too; change that under Record / import." : ""),
            current ?? (MicrophoneName.Trim().Length > 0 ? MicrophoneName.Trim() : ""), SpeakerNames.Where(x => x != UnknownSpeaker), "Name microphone lines");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunForSessionAsync("Naming the microphone lines…", async (sessionId, _) =>
        {
            var count = await Task.Run(() => controller.SetMicrophoneSpeaker(sessionId, name));
            if (MicrophoneName.Trim().Length == 0) MicrophoneName = name.Trim();
            SetStatus($"All {count:N0} microphone line(s) are now \"{name.Trim()}\"; new microphone lines get this name too.");
        });
    }

    private async Task MergeSessionsInteractiveAsync()
    {
        var preselected = SelectedSessions.Select(session => session.Id).ToArray();
        if (preselected.Length == 0 && SelectedSession is { } current) preselected = [current.Id];
        var ids = await dialogs.ChooseSessionsToMergeAsync(Sessions.ToArray(), preselected, controller.RecordingSessionId);
        if (ids is { Count: > 1 }) await MergeSessionsAsync(ids);
    }

    public bool CanMergeSelectedSessions => SelectedSessions.Count > 1 && !Busy && !closing && !SelectedSessions.Any(s => IsRecordingSession(s.Id));

    public async Task MergeSelectedSessionsAsync()
    {
        if (!CanMergeSelectedSessions) return;
        var chosen = SelectedSessions.OrderBy(session => session.CreatedUtc).ToArray();
        var later = string.Join("\n", chosen.Skip(1).Take(12).Select(s => $"  • {s.Name} ({s.CreatedUtc.ToLocalTime():MMM d · HH:mm})")) +
            (chosen.Length > 13 ? $"\n  …and {chosen.Length - 13:N0} more" : "");
        if (!await dialogs.ConfirmAsync("Merge sessions?",
                $"Merge {chosen.Length:N0} sessions into \"{chosen[0].Name}\" ({chosen[0].CreatedUtc.ToLocalTime():MMM d · HH:mm})?\n\n" +
                $"These follow it, in recording order:\n{later}\n\n" +
                $"Together they run {TranscriptPresentation.Duration(chosen.Sum(s => s.DurationTicks))}. They stop being separate sessions. This cannot be undone."))
            return;
        await MergeSessionsAsync(chosen.Select(session => session.Id).ToArray());
    }

    private async Task ResplitAsync()
    {
        if (SelectedSession is not { } session) return;
        if (!await dialogs.ConfirmAsync("Re-transcribe this session?",
                $"Re-transcribe \"{session.Name}\" ({TranscriptPresentation.Duration(session.DurationTicks)})?\n\n" +
                $"Its audio is cut again into phrases using the current pause setting ({PhrasePauseMilliseconds:N0} ms, on Record / import) " +
                "and transcribed again from scratch. Speaker analysis then runs again.\n\n" +
                "  • Speakers you set by hand carry over to the new lines covering the same time.\n" +
                "  • Text edits you made to lines are discarded.\n" +
                "  • Long recordings take a while; lines come back as each phrase is done (see Jobs).\n\n" +
                "When it finishes, press \"Fill speakers from my labels\" to relabel everything else."))
            return;
        await RunForSessionAsync("Re-cutting the audio into phrases…", async (id, token) =>
        {
            try { await controller.ResplitSessionAsync(id, new Progress<string>(message => SetStatus(message)), token); }
            catch (InvalidOperationException error) { SetStatus(error.Message, true); }
        });
    }

    public Task MergeSessionsAsync(IReadOnlyCollection<Guid> ids) =>
        RunAsync($"Merging {ids.Count:N0} sessions…", async token =>
        {
            StoredSession merged;
            try { merged = await controller.MergeSessionsAsync(ids, token); }
            catch (InvalidOperationException error) { SetStatus(error.Message, true); return; }
            // Rewrite the live file with the stitched transcript if it mirrored one of the parts (or is armed and idle).
            if (liveSessionId is { } live ? ids.Contains(live) : LiveFileEnabled) StartLiveFile(merged.Id);
            RefreshLibrary();
            SelectedSession = Sessions.FirstOrDefault(x => x.Id == merged.Id) ?? merged;
            LoadTranscript();
            SetStatus($"Merged {ids.Count:N0} sessions into \"{merged.Name}\". The transcript now runs through every part in recording order.");
        });

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
        var path = await dialogs.OpenAudioAsync();
        if (path is null || SelectedProvider is not { } provider) return;
        var name = TakeSessionName();
        var locale = Language.Trim();
        var consent = NewCloudConsent;
        SaveRecordingPreferences();
        await RunAsync("Probing the selected media file…", async token =>
        {
            var probe = await controller.ProbeMediaAsync(path, token);
            if (probe.Streams.Count == 0) throw new InvalidDataException("This file has no audio stream.");
            var stream = probe.Streams.Count == 1 ? probe.Streams[0] : await dialogs.ChooseStreamAsync(probe);
            if (stream is null) { SetStatus("Import not started."); return; }
            SetStatus($"Importing stream {stream.Index}. Retaining a managed original; preparing audio…");
            NewCloudConsent = false;
            var session = await controller.ImportAudioAsync(name, path, stream.Index, provider.Id, locale, consent, token);
            if (sessionNameIsDefault) ResetSessionName();
            if (LiveFileEnabled) StartLiveFile(session.Id);
            RefreshLibrary();
            SelectedSession = Sessions.FirstOrDefault(x => x.Id == session.Id) ?? session;
            SetStatus("Audio imported. Jobs and originals are available in the selected session.");
        });
    }

    private async Task ImportVttAsync()
    {
        var path = await dialogs.OpenVttAsync();
        if (path is not null)
            await RunForSessionAsync("Importing local WebVTT cues…", (id, token) => controller.ImportVttAsync(id, path, token));
    }

    private async Task FetchTeamsAsync()
    {
        var request = await dialogs.RequestTeamsTranscriptAsync();
        if (request is null) return;
        try
        {
            await RunForSessionAsync("Connecting to configured Microsoft Graph transcript access…",
                (id, token) => controller.FetchTeamsTranscriptAsync(id, request,
                    prompt => dialogs.ShowDeviceSignInAsync(prompt, () => operationCancellation?.Cancel()), token));
        }
        finally { dialogs.CloseDeviceSignIn(); }
    }

    private async Task InstallModelsAsync()
    {
        if (!await dialogs.ConfirmAsync("Install local diarization models?",
            "Download 33,488,994 bytes (about 33.49 MB) of segmentation and speaker-embedding release artifacts?\n\nSegmentation: MIT (CNRS 2023); embedding: CC BY 4.0. Attribution and package notices are retained. These release files need no Hugging Face token; the original Hugging Face segmentation distribution is gated.\n\nNo audio is uploaded. No large Whisper model is included.")) return;
        await RunAsync("Downloading the explicitly requested local diarization models…", async token =>
        {
            await controller.InstallDiarizationModelsAsync(new Progress<string>(message => ModelStatus = message), token);
            ModelStatus = controller.DiarizationModelsReady ? "Local diarization models are installed and ready." : "Model installation did not report ready.";
            CommandManager.InvalidateRequerySuggested();
        });
    }

    private async Task ChooseWhisperModelAsync()
    {
        var path = await dialogs.OpenWhisperModelAsync();
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

    private async Task InstallWhisperModelAsync()
    {
        var model = RecommendedWhisper;
        if (!await dialogs.ConfirmAsync("Install the recommended Whisper model?",
            $"Download {model.FileName} ({model.Bytes:N0} bytes, about {model.Bytes / 1073741824d:N2} GiB) from the pinned whisper.cpp Hugging Face revision?\n\n" +
            $"Whisper {model.Id} is the size recommended for this PC's hardware. MIT license (OpenAI Whisper; GGML conversion by whisper.cpp contributors). SHA256 is verified before use and the model is selected automatically.\n\nNo audio is uploaded."))
            return;
        await RunAsync("Downloading the recommended local Whisper model…", async token =>
        {
            await controller.InstallWhisperModelAsync(model.Id, new Progress<string>(message => LocalModel = message), token);
            if (controller.WhisperModelPath is { } path) LocalModel = DescribeModel(path);
            SetStatus($"Whisper {model.Id} installed and selected for local transcription.");
        });
    }

    private Task InstallParakeetModelAsync() => RunAsync("Downloading the Parakeet transcription model…", async token =>
    {
        await controller.InstallParakeetModelAsync(new Progress<string>(message => ParakeetStatus = message), token);
        ParakeetStatus = ParakeetReadyText;
        SetStatus("Parakeet TDT v3 is installed. Parakeet sessions transcribe on this PC.");
    });

    private string ParakeetReadyText => controller.ParakeetModelReady
        ? "Parakeet TDT v3 is installed (about 640 MB on disk, ~1 GB RAM while running)."
        : "The Parakeet model is not installed yet. It downloads automatically at startup (465 MiB); use the button to retry.";

    /// <summary>The one-time GPU question: large NVIDIA download plus license terms. "No" keeps Parakeet on the CPU.</summary>
    private async Task AskGpuAsync(string? gpu)
    {
        if (closing) return;
        var download = AudioTranscriber.Diarization.ParakeetGpuPackage.DownloadBytes / 1e9;
        var disk = AudioTranscriber.Diarization.ParakeetGpuPackage.InstalledBytes / 1e9;
        var yes = await dialogs.ConfirmAsync("Run Parakeet on your NVIDIA GPU?",
            $"{(gpu is null ? "If this PC has a supported NVIDIA GPU, " : $"This PC has an NVIDIA {gpu}. ")}Parakeet can run on it instead of the CPU: " +
            "several times faster on long imports and with almost no CPU load. Accuracy is the same.\n\n" +
            $"This needs a one-time download of about {download:0.0} GB ({disk:0.0} GB on disk): NVIDIA's CUDA runtime, cuBLAS and cuDNN, " +
            "the CUDA build of ONNX Runtime (from sherpa-onnx), and the full-precision Parakeet TDT v3 model. Every file is SHA256-verified " +
            "and kept in the app's model folder; nothing is installed system-wide and no admin rights are needed. Audio never leaves this PC.\n\n" +
            "By choosing Yes you accept the license terms of these components:\n" +
            AudioTranscriber.Diarization.ParakeetGpuPackage.LicenseNotice + "\n" +
            "Choose No to keep using the CPU (fast already). You can change this later in Privacy / models.");
        controller.SetGpuParakeet(yes);
        LocalGpuStatus = controller.LocalGpuStatus ?? LocalGpuStatus;
        SetStatus(yes ? "Downloading the GPU runtime for Parakeet in the background; transcription keeps using the CPU until it's ready."
            : "Parakeet keeps using the CPU. You can switch to the GPU later in Privacy / models.");
        CommandManager.InvalidateRequerySuggested();
    }

    public void SaveKey(string key) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Enter a NVIDIA API key first.");
        controller.SetNvidiaKey(key, RememberKey);
        Changed(nameof(KeyStatus));
        SetStatus(RememberKey ? $"Key remembered, {AudioTranscriber.Providers.UserSecretProtection.Description}." : "Key kept in memory only.");
    });

    private void GrantConsent()
    {
        if (SelectedSession is null || !SelectedCloudConsent) return;
        SessionAction(id => controller.SetCloudConsent(id, true), "Consent granted for this session only. Queued NVIDIA work may upload its audio tracks.");
        SelectedCloudConsent = false;
    }

    private async Task CancelJobsAsync()
    {
        if (!await dialogs.ConfirmAsync("Cancel transcription jobs?", "Cancel pending and active transcription jobs for the selected session? Original audio is retained. This is not the recording Stop button.")) return;
        SessionAction(controller.CancelTranscription, "Transcription jobs canceled. Recording, if active, is unaffected.");
    }

    private void SessionAction(Action<Guid> action, string message) => Guard(() =>
    {
        if (SelectedSession is not { } session) return;
        action(session.Id);
        RefreshLibrary();
        SetStatus(message);
    });

    public bool IsRecordingSession(Guid sessionId) => controller.RecordingSessionId == sessionId;
    public bool CanDeleteSession(StoredSession session) => !Busy && !closing && !IsRecordingSession(session.Id);

    public async Task DeleteSessionAsync(StoredSession session)
    {
        if (IsRecordingSession(session.Id))
        {
            SetStatus("Stop the recording before deleting its session.", true);
            return;
        }
        if (!CanDeleteSession(session)) return;
        if (!await dialogs.ConfirmAsync("Delete session?",
                $"Permanently delete \"{session.Name}\" ({session.CreatedUtc.ToLocalTime():MMM d, yyyy · HH:mm}, {TranscriptPresentation.Duration(session.DurationTicks)})?\n\n" +
                "Its transcript, speakers, jobs and retained original audio are removed from this PC. This cannot be undone.\n" +
                "Exported transcripts and the live transcript file are not touched."))
            return;
        await DeleteAsync([session.Id]);
    }

    private async Task DeleteSessionsInteractiveAsync()
    {
        var ids = await dialogs.ChooseSessionsToDeleteAsync(Sessions.ToArray(), controller.RecordingSessionId);
        if (ids is { Count: > 0 }) await DeleteAsync(ids);
    }

    public bool CanDeleteSelectedSessions => SelectedSessions.Count > 0 && !Busy && !closing && !SelectedSessions.Any(s => IsRecordingSession(s.Id));

    public async Task DeleteSelectedSessionsAsync()
    {
        var chosen = SelectedSessions.ToArray();
        if (chosen.Length == 1) { await DeleteSessionAsync(chosen[0]); return; }
        if (chosen.Any(s => IsRecordingSession(s.Id)))
        {
            SetStatus("Stop the recording before deleting its session.", true);
            return;
        }
        if (!CanDeleteSelectedSessions) return;
        var list = string.Join("\n", chosen.Take(12).Select(s => $"  • {s.Name} ({s.CreatedUtc.ToLocalTime():MMM d, yyyy · HH:mm})")) +
            (chosen.Length > 12 ? $"\n  …and {chosen.Length - 12:N0} more" : "");
        if (!await dialogs.ConfirmAsync("Delete sessions?",
                $"Permanently delete these {chosen.Length:N0} sessions?\n\n{list}\n\n" +
                "Their transcripts, speakers, jobs and retained original audio are removed from this PC. This cannot be undone.\n" +
                "Exported transcripts and the live transcript file are not touched."))
            return;
        await DeleteAsync(chosen.Select(session => session.Id).ToArray());
    }

    private Task DeleteAsync(IReadOnlyCollection<Guid> ids) =>
        RunAsync(ids.Count == 1 ? "Deleting the session…" : $"Deleting {ids.Count:N0} sessions…", async token =>
        {
            // Move off a doomed selection first so nothing keeps reading it; prefer the next session down, like Explorer.
            if (SelectedSession is { } current && ids.Contains(current.Id))
            {
                var index = Sessions.IndexOf(current);
                SelectedSession = Sessions.Skip(index + 1).Concat(Sessions.Take(Math.Max(index, 0)).Reverse())
                    .FirstOrDefault(session => !ids.Contains(session.Id));
            }
            if (liveSessionId is { } live && ids.Contains(live)) StopLiveFile();
            await controller.DeleteSessionsAsync(ids, token);
            RefreshLibrary();
            LoadTranscript();
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
        pendingSeek = ticks;
        scrollToTop = true;
        LoadTranscript();
    });

    // Loads the whole transcript (or the whole search/speaker filter result); the grid virtualizes its rows.
    private void LoadTranscript()
    {
        var rowId = SelectedRow?.Row.Id;
        var selectedIds = SelectedRows.SelectMany(item => item.Rows).Select(row => row.Id).ToHashSet();
        var rows = SelectedSession is { } session
            ? controller.Store.EnumerateTranscript(session.Id, appliedSearch, appliedSpeaker)
            : [];
        // Merge same-speaker rows only in the full transcript; a search or speaker filter hides rows in between.
        var merge = string.IsNullOrEmpty(appliedSearch) && appliedSpeaker is null;
        var lines = merge ? TranscriptLine.Group(rows) : rows.Select(row => new TranscriptLine(row));
        var items = lines.Select(x => new TranscriptItem(x, Tracks.FirstOrDefault(t => t.Id == x.First.TrackId)?.Name ?? x.First.TrackId.ToString())).ToList();
        TranscriptReloading?.Invoke();
        reloadingTranscript = true;
        try { Transcript.Update(items); }
        finally { reloadingTranscript = false; }
        SelectedRow = rowId is null ? null : Transcript.FirstOrDefault(x => x.Contains(rowId));
        SelectedRows = Transcript.Where(item => item.Rows.Any(row => selectedIds.Contains(row.Id))).ToArray();
        var reset = scrollToTop;
        scrollToTop = false;
        var target = pendingSeek is { } seekTicks ? Transcript.FirstOrDefault(item => item.EndTicks >= seekTicks) : null;
        pendingSeek = null;
        TranscriptReloaded?.Invoke(reset, target);
        Changed(nameof(TranscriptSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SaveCorrection() => Guard(() =>
    {
        if (SelectedRow is not { } item) return;
        foreach (var (id, text) in item.Line.Corrections(Correction)) controller.Store.CorrectSegment(id, text);
        LoadTranscript();
        SetStatus("Correction saved. Raw recognition and provenance are unchanged.");
    });

    private void RestoreRaw() => Guard(() =>
    {
        if (SelectedRow is not { } item) return;
        foreach (var row in item.Rows.Where(row => row.Correction is not null)) controller.Store.CorrectSegment(row.Id, null);
        LoadTranscript();
        SetStatus("Correction removed; raw recognition is shown again.");
    });

    private const string UnknownSpeaker = "Unknown / unassigned";

    private async Task RenameSpeakerAsync(StoredSpeaker? speaker, string name)
    {
        if (SelectedSession is not { } session || speaker is null || string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        var existing = Speakers.FirstOrDefault(x => x.Id != speaker.Id && string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !await dialogs.ConfirmAsync("Merge speakers?",
                $"\"{existing.Name}\" already exists. Giving \"{speaker.Name}\" the same name merges them into one speaker:\n\n" +
                "• every line of both becomes \"" + existing.Name + "\"\n" +
                "• their voice samples are combined, so future lines match either voice\n" +
                "• lines that are still Unknown are re-checked with the combined voice\n\nMerge them?"))
            return;
        try
        {
            var kept = await controller.RenameSpeakerAsync(session.Id, speaker.Id, name);
            RefreshSessionDetails();
            RefreshVoices();
            ManagedSpeaker = Speakers.FirstOrDefault(x => x.Id == kept);
            LoadTranscript();
            SetStatus(existing is null
                ? $"Renamed to \"{name}\" throughout this session."
                : $"Merged \"{speaker.Name}\" into \"{existing.Name}\". Their voices now count as one speaker.");
        }
        catch (Exception error) { Report(error); }
    }

    private void SetLineSpeaker()
    {
        var typed = LineSpeaker.Trim();
        if (typed.Length == 0 || string.Equals(typed, UnknownSpeaker, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typed, "Unknown", StringComparison.OrdinalIgnoreCase))
            AssignSelection(null, null);
        else
            AssignSelection(null, typed);
    }

    /// <summary>Labels the selected lines with an existing speaker (by ID), a typed name, or Unknown when both are null.</summary>
    public void AssignSelection(string? speakerId, string? newName) => Guard(() =>
    {
        if (SelectedSession is not { } session || SelectedRows.Count == 0) return;
        var rows = SelectedRows;
        StoredSpeaker? speaker = null;
        if (newName is not null) speaker = controller.GetOrCreateSpeaker(session.Id, newName);
        else if (speakerId is not null) speaker = Speakers.FirstOrDefault(x => x.Id == speakerId);
        controller.AssignSpeaker(session.Id, rows.SelectMany(item => item.Rows).Select(row => row.Id).ToArray(), speaker?.Id);
        RefreshSessionDetails();
        LoadTranscript();
        LineSpeaker = SelectedRow?.Speaker ?? LineSpeaker;
        var lines = rows.Count == 1 ? "1 line" : $"{rows.Count} lines";
        SetStatus(speaker is null
            ? $"Marked {lines} as Unknown."
            : controller.DiarizationModelsReady
                ? $"Set {lines} to \"{speaker.Name}\". Learning this voice so later lines are labeled automatically…"
                : $"Set {lines} to \"{speaker.Name}\".");
    });

    public async void AssignSelectionToNewSpeaker()
    {
        if (SelectedRows.Count == 0) return;
        var name = await dialogs.PromptSpeakerNameAsync("Set speaker",
            $"Who is speaking in the selected {(SelectedRows.Count == 1 ? "line" : SelectedRows.Count + " lines")}? Type a new name or pick an existing one. " +
            "Using an existing name adds these lines to that speaker.",
            "", Speakers.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase), "Set speaker");
        if (name is not null) AssignSelection(null, name);
    }

    public async Task RenameSpeakerInteractiveAsync(string speakerId)
    {
        var speaker = Speakers.FirstOrDefault(x => x.Id == speakerId);
        if (speaker is null) return;
        var name = await dialogs.PromptSpeakerNameAsync("Rename speaker",
            $"New name for \"{speaker.Name}\" on every line of this session. Choosing another speaker's name merges the two.",
            speaker.Name, Speakers.Where(x => x.Id != speakerId).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase), "Rename");
        if (name is not null && name != speaker.Name) await RenameSpeakerAsync(speaker, name);
    }

    private void RefreshVoices()
    {
        var voices = controller.GetVoiceLibrary();
        var stamp = string.Join("|", voices.Select(voice => $"{voice.Id}:{voice.UpdatedUtc.UtcTicks}:{voice.Summary}"));
        if (stamp == voiceStamp) return;
        voiceStamp = stamp;
        var selectedId = SelectedVoice?.Id;
        Replace(Voices, voices);
        SelectedVoice = Voices.FirstOrDefault(voice => voice.Id == selectedId);
        Changed(nameof(VoiceLibrarySummary));
        CommandManager.InvalidateRequerySuggested();
    }

    private Task RecognizeVoicesAsync() => RunForSessionAsync("Matching this session's speakers against remembered voices…", async (id, token) =>
    {
        var names = await controller.RecognizeKnownVoicesAsync(id, token);
        SetStatus(names.Count > 0
            ? $"Recognized {string.Join(", ", names)} by voice."
            : "No unnamed speaker in this session matched a remembered voice closely enough, so nothing was renamed.");
    });

    private Task RememberSessionVoicesAsync() => RunForSessionAsync("Remembering this session's named voices…", async (id, token) =>
    {
        var names = await controller.RememberSessionVoicesAsync(id, token);
        RefreshVoices();
        SetStatus(names.Count > 0
            ? $"Remembered {string.Join(", ", names)}. Future recordings name them automatically."
            : "No named speaker in this session has a voice profile yet. Run speaker analysis, then name speakers or label a few of their lines.");
    });

    private Task RememberAllVoicesAsync() => RunAsync("Remembering the named voices from every session…", async token =>
    {
        var names = await controller.RememberAllSessionVoicesAsync(false, token);
        RefreshVoices();
        SetStatus(names.Count > 0
            ? $"Remembered {string.Join(", ", names)} from past sessions. Future recordings name them automatically."
            : "No named speaker in any session has a voice profile yet. Run speaker analysis, then name speakers or label a few of their lines.");
    });

    // Once per library: learns speakers named before the voice library existed. Runs quietly in the background.
    private async Task BackfillVoicesOnceAsync()
    {
        try { await Task.Run(() => controller.RememberAllSessionVoicesAsync(true)); }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!closing) SetStatus("Could not learn voices from past sessions: " + error.Message, true); }
    }

    private async Task RenameVoiceAsync()
    {
        if (SelectedVoice is not { } voice) return;
        var name = await dialogs.PromptSpeakerNameAsync("Rename remembered voice",
            $"New name for the remembered voice \"{voice.Name}\". Future recordings use it; past sessions keep their names. " +
            "Choosing another remembered name combines the two voices.",
            voice.Name, Voices.Where(item => item.Id != voice.Id).Select(item => item.Name), "Rename");
        if (name is null || name == voice.Name) return;
        Guard(() =>
        {
            controller.RenameVoice(voice.Id, name);
            RefreshVoices();
            SetStatus($"The remembered voice is now \"{name}\".");
        });
    }

    private async Task ForgetVoiceAsync()
    {
        if (SelectedVoice is not { } voice) return;
        if (!await dialogs.ConfirmAsync("Forget this voice?",
                $"Delete the remembered voice \"{voice.Name}\"?\n\nFuture recordings will no longer name them automatically. " +
                "Past sessions keep their speaker names and their own in-session voice profiles.")) return;
        Guard(() =>
        {
            controller.ForgetVoice(voice.Id);
            RefreshVoices();
            SetStatus($"Forgot \"{voice.Name}\"'s voice.");
        });
    }

    private async Task ForgetAllVoicesAsync()
    {
        if (!await dialogs.ConfirmAsync("Forget all voices?",
                $"Delete all {Voices.Count} remembered voice{(Voices.Count == 1 ? "" : "s")}?\n\nFuture recordings will no longer name anyone automatically " +
                "until you name speakers again. Past sessions keep their speaker names and their own in-session voice profiles.")) return;
        Guard(() =>
        {
            var count = controller.ForgetAllVoices();
            RefreshVoices();
            SetStatus($"Forgot {count} remembered voice{(count == 1 ? "" : "s")}.");
        });
    }

    public void PlaySelectedRow()
    {
        if (PlayRowCommand.CanExecute(null)) PlayRowCommand.Execute(null);
    }

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

    private async Task ExportAsync()
    {
        if (SelectedSession is not { } session) return;
        var path = await dialogs.SaveExportAsync(session.Name);
        if (path is not null)
            await RunAsync("Exporting the complete transcript, one bounded page at a time…",
                token => TranscriptExporter.ExportAsync(controller.Store, session.Id, path, token));
    }

    private Task RunForSessionAsync(string message, Func<Guid, CancellationToken, Task> action)
    {
        if (SelectedSession is not { } session) return Task.CompletedTask;
        return RunAsync(message, async token =>
        {
            await action(session.Id, token);
            RefreshLibrary();
            if (SelectedSession?.Id == session.Id) LoadTranscript();
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
        AppLog.Error("Operation failed.", error);
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
    private void SetStatus(string message, bool error = false, bool persist = true)
    {
        Status = message;
        StatusIsError = error;
        if (error) Log(message, ActivityKind.Error, persist: persist);
    }
    private void OnNotification(AppNotification notification) => dispatcher.Post(() =>
    {
        if (!closing)
        {
            // The controller already wrote this notification to the diagnostic log.
            SetStatus(notification.Message, notification.IsError, persist: false);
            if (!notification.IsError) LogNotification(notification.Message);
            Changed(nameof(IsRecording)); Changed(nameof(CaptureState));
        }
    });
    private void OnLevelsChanged(CaptureMeter levels)
    {
        Volatile.Write(ref pendingMeter, levels);
        if (Interlocked.Exchange(ref meterQueued, 1) != 0) return;
        dispatcher.Post(() =>
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
        updateTimer.Stop();
        updateCancellation.Cancel();
        Templates.Shutdown();
        SetStatus("Closing: waiting for operations and original audio tails to finish…");
        try
        {
            operationCancellation?.Cancel();
            if (activeOperation is not null) await activeOperation;
            if (stopTask is not null) await stopTask;
            if (controller.IsRecording) await controller.StopRecordingAsync();
            await Discord.ShutdownAsync();
            if (liveWrite is not null) await liveWrite;
            if (liveSessionId is { } liveId && liveTargetPath is { Length: > 0 } livePath)
            {
                try { await Task.Run(() => LiveTranscriptFile.Write(livePath, LiveTranscriptFile.Render(controller.Store, liveId))); }
                catch { }
            }
            controller.StopPlayback();
            await controller.DisposeAsync();
            controller.Notification -= OnNotification;
            controller.LevelsChanged -= OnLevelsChanged;
            controller.TranscriptChanged -= OnTranscriptChanged;
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
