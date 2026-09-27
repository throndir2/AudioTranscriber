using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using AudioTranscriber.Audio;
using AudioTranscriber.Core;
using AudioTranscriber.Diarization;
using AudioTranscriber.Integrations;
using AudioTranscriber.Providers;
using AudioTranscriber.Storage;
using Core = AudioTranscriber.Core;

namespace AudioTranscriber.Application;

public sealed class AppController : IAppController
{
    private const string DiarizationProvider = "local-diarization";
    // Hosted route used when local Whisper is unsure; see docs/providers.md for how the threshold was chosen.
    private const string FallbackProviderId = "nvidia-parakeet-tdt-v3";
    private const double DefaultFallbackBelowConfidence = 0.85;
    private volatile string? fallbackSuspended;
    // Parakeet on this PC's NVIDIA GPU (NVIDIA speech NIM in Docker), when the machine can run it.
    private volatile LocalRivaProvider? localGpu;
    private volatile LocalNimHost? nimHost;
    private Task? localGpuStart;
    private volatile string? localGpuStatus, localGpuSetupStatus;
    private Task? whisperSetup;
    private volatile string? whisperSetupStatus;
    private volatile bool whisperSetupFailed;
    private const int LiveChunkMaxSeconds = 6;
    private const int LivePauseSplitAfterMilliseconds = 1500;
    private readonly IAudioCaptureService capture;
    private readonly IMediaNormalizer media;
    private readonly IAudioPlaybackService playback;
    private readonly TimelinePlayer? timelinePlayer;
    private readonly NvidiaCredentialVault credentials = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly SemaphoreSlim registryGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Task> backgroundTasks = new();
    private readonly WakeSignal wake = new();
    private readonly ConcurrentDictionary<Guid, Task> mediaTasks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> mediaCancellation = new();
    private readonly ConcurrentDictionary<Guid, CaptureFeed> captureFeeds = new();
    private readonly Dictionary<string, ITranscriptionProvider> providerCache = new();
    private readonly System.Diagnostics.Stopwatch recordingClock = new();
    private readonly object settingsGate = new();
    private readonly object setupGate = new();
    private Task? modelSetup;
    private volatile string? setupStatus;
    private readonly FileStream libraryLock;
    private readonly string modelDirectory;
    private readonly string settingsPath;
    private readonly string credentialPath;
    private readonly Func<string, ITranscriptionProvider>? providerOverride;
    private readonly Func<IDiarizationService>? diarizationOverride;
    // Speech recognition and speaker analysis run in independent lanes so text is never queued behind speaker work.
    private readonly JobLane speechLane = new(), speakerLane = new();
    private readonly Task scheduler, speakerScheduler;
    private Guid? microphoneTrackId;
    private volatile LocalSettings settings;
    private bool captureFaulted;
    private bool stopping;
    private bool disposed;
    private double outputLevel, microphoneLevel;
    public LibraryStore Store { get; }
    public bool IsRecording => RecordingSessionId is not null;
    public Guid? RecordingSessionId { get; private set; }
    public bool HasNvidiaKey { get; private set; }
    public bool DiarizationModelsReady
    {
        get
        {
            var paths = DiarizationModelPaths.InDirectory(modelDirectory);
            return diarizationOverride is not null ||
                   (File.Exists(paths.SegmentationModelPath) && File.Exists(paths.EmbeddingModelPath));
        }
    }
    public IReadOnlyList<ProviderOption> Providers { get; } = NvidiaModelCatalog.All
        .Select(model => new ProviderOption(model.Id, model.DisplayName, true,
            model.VerifiedWordTiming ? "Word timestamps when returned" : "Coarse audio-chunk timestamps"))
        .Append(new(SherpaParakeetProvider.ProviderId, "Local Parakeet TDT v3 · recommended (runs on this PC; 25 European languages)", false, "Word timestamps"))
        .Append(new("local-whisper", "Local Whisper · large-v3-turbo (any language; GPU via Vulkan when available)", false, "Segment timestamps"))
        .ToArray();
    public event Action<AppNotification>? Notification;
    public event Action<CaptureMeter>? LevelsChanged;
    public event Action<Guid>? TranscriptChanged;

    public AppController(string rootDirectory, IAudioCaptureService? capture = null,
        IMediaNormalizer? media = null, IAudioPlaybackService? playback = null,
        Func<string, ITranscriptionProvider>? providerFactory = null,
        Func<IDiarizationService>? diarizationFactory = null)
    {
        var root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        libraryLock = new(Path.Combine(root, "library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Store = new(root);
        Store.RecoverInterruptedJobs();
        settingsPath = Path.Combine(root, "preferences.json");
        credentialPath = Path.Combine(root, "nvidia.dpapi");
        if (File.Exists(settingsPath) && new FileInfo(settingsPath).Length > 16_384)
            throw new InvalidDataException("The local preferences file exceeds its size limit.");
        settings = File.Exists(settingsPath)
            ? JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(settingsPath))
              ?? throw new InvalidDataException("The local preferences file is invalid.")
            : new(null);
        var checkout = FindCheckout();
        modelDirectory = checkout is null ? Path.Combine(root, "models", "diarization")
            : Path.Combine(checkout, ".models", "diarization");
        if (settings.WhisperModelPath is null || !File.Exists(settings.WhisperModelPath))
        {
            var recommended = Path.Combine(WhisperModelDirectory, LocalWhisperModelCatalog.Recommended.FileName);
            if (File.Exists(recommended)) settings = settings with { WhisperModelPath = recommended };
        }
        if (File.Exists(credentialPath))
        {
            Task.Run(() => credentials.LoadForCurrentUserAsync(credentialPath)).GetAwaiter().GetResult();
            HasNvidiaKey = true;
        }
        this.capture = capture ?? new WasapiAudioCaptureService();
        this.media = media ?? new FfmpegMediaNormalizer();
        this.playback = playback ?? new AudioPlaybackService();
        if (playback is null)
        {
            timelinePlayer = new TimelinePlayer();
            timelinePlayer.PlaybackFailed += error => Notify("Playback failed: " + error.Message, true);
        }
        providerOverride = providerFactory;
        diarizationOverride = diarizationFactory;
        this.capture.ChunkSealed += OnNativeChunk;
        this.capture.Levels += OnLevels;
        this.capture.Fault += OnCaptureFault;
        if (this.playback is AudioPlaybackService player)
            player.PlaybackFailed += error => Notify("Playback failed: " + error.Message, true);
        scheduler = Task.Run(() => SchedulerAsync(speechLane, null, DiarizationProvider));
        speakerScheduler = Task.Run(() => SchedulerAsync(speakerLane, DiarizationProvider, null));
    }

    public IReadOnlyList<DeviceChoice> GetOutputDevices() => capture.GetOutputDevices()
        .Where(device => device.IsAvailable).OrderByDescending(device => device.IsDefault)
        .Select(device => new DeviceChoice(device.Id, device.Name)).ToArray();

    public IReadOnlyList<DeviceChoice> GetMicrophoneDevices() => capture.GetMicrophoneDevices()
        .Where(device => device.IsAvailable).OrderByDescending(device => device.IsDefault)
        .Select(device => new DeviceChoice(device.Id, device.Name)).ToArray();

    public void SetNvidiaKey(string key, bool remember)
    {
        credentials.SetMemoryOnly(key);
        HasNvidiaKey = true;
        fallbackSuspended = null;
        if (localGpu is null && modelSetup is not null) StartLocalGpu();
        if (remember)
            Task.Run(() => credentials.SaveForCurrentUserAsync(credentialPath, true)).GetAwaiter().GetResult();
        else if (File.Exists(credentialPath)) File.Delete(credentialPath);
        Notify(remember ? "NVIDIA key protected for this Windows user." : "NVIDIA key held in memory only.");
    }

    public void ClearNvidiaKey()
    {
        credentials.Clear();
        HasNvidiaKey = false;
        if (File.Exists(credentialPath)) File.Delete(credentialPath);
        Notify("NVIDIA key cleared. Queued cloud work will require a key again.");
    }

    public void SetLocalWhisperModel(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Choose an existing local Whisper model.");
        UpdateSettings(current => current with { WhisperModelPath = Path.GetFullPath(path) });
        Store.ReleaseProviderJobs("local-whisper");
        wake.Release();
        Notify("Local Whisper model selected. Queued local transcription continues with it.");
    }

    public string? SetupStatus => setupStatus ?? whisperSetupStatus ?? localGpuSetupStatus;
    public string? LocalGpuStatus => localGpuStatus;
    public bool ModelSetupRunning => modelSetup is { IsCompleted: false } || whisperSetup is { IsCompleted: false };
    public bool ParakeetModelReady => ParakeetModels.IsInstalled(ParakeetModelDirectory);
    private string ParakeetModelDirectory => Path.Combine(Path.GetDirectoryName(modelDirectory)!, "parakeet");

    /// <summary>Downloads the default speaker and Parakeet models when missing, then releases work that waited for them.</summary>
    public Task EnsureDefaultModelsAsync()
    {
        lock (setupGate)
        {
            if (modelSetup is { IsCompleted: false } running) return running;
            return modelSetup = Task.Run(SetupDefaultModelsAsync);
        }
    }

    public void RetryBlockedLocalWork()
    {
        if (WhisperModelPath is not null) Store.ReleaseProviderJobs("local-whisper");
        if (ParakeetModelReady) Store.ReleaseProviderJobs(SherpaParakeetProvider.ProviderId);
        if (DiarizationModelsReady) Store.ReleaseProviderJobs(DiarizationProvider);
        wake.Release();
    }

    public async Task InstallParakeetModelAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await ParakeetModels.InstallAsync(ParakeetModelDirectory, progress, cancellationToken);
        progress?.Report("Parakeet TDT v3 is installed and verified.");
        Store.ReleaseProviderJobs(SherpaParakeetProvider.ProviderId);
        wake.Release();
    }

    private async Task SetupDefaultModelsAsync()
    {
        var token = shutdown.Token;
        try
        {
            if (!DiarizationModelsReady)
            {
                setupStatus = "Downloading the speaker-labeling models (33 MB)…";
                await DiarizationModels.InstallAsync(modelDirectory, null, token);
                Notify("Speaker-labeling models downloaded and verified.");
            }
            Store.ReleaseProviderJobs(DiarizationProvider);
            wake.Release();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { setupStatus = null; return; }
        catch (Exception error)
        {
            Notify("Speaker-labeling models could not be downloaded automatically (" + error.Message +
                "). Transcription still works; retry from Privacy / models.", true);
        }
        try
        {
            if (!ParakeetModelReady)
            {
                setupStatus = $"Downloading the Parakeet transcription model ({ParakeetModels.ArchiveBytes / 1_048_576:N0} MiB)…";
                await ParakeetModels.InstallAsync(ParakeetModelDirectory, new InlineProgress<string>(message => setupStatus =
                    message + " Recording works now; queued audio is transcribed as soon as it finishes."), token);
                Notify("Parakeet TDT v3 downloaded and verified. Queued audio is being transcribed.");
            }
            Store.ReleaseProviderJobs(SherpaParakeetProvider.ProviderId);
            wake.Release();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Notify("The Parakeet model could not be downloaded automatically (" + error.Message +
                "). Recording still works; install it from Privacy / models, or choose Local Whisper.", true);
        }
        finally { setupStatus = null; }
        if (WhisperModelPath is not null) Store.ReleaseProviderJobs("local-whisper");
        StartLocalGpu();
    }

    /// <summary>Whisper is optional now: its model downloads the first time a Whisper session needs it.</summary>
    private bool StartWhisperDownload()
    {
        if (providerOverride is not null || WhisperModelPath is not null) return false;
        lock (setupGate)
        {
            if (whisperSetup is { IsCompleted: false }) return true;
            if (whisperSetupFailed) return false;
            whisperSetup = Task.Run(async () =>
            {
                var token = shutdown.Token;
                var model = LocalWhisperModelCatalog.Recommended;
                var totalMiB = model.Bytes / 1048576;
                try
                {
                    whisperSetupStatus = $"Downloading the Whisper transcription model ({totalMiB:N0} MiB)…";
                    var path = await VerifiedModelDownload.InstallWhisperAsync(model, WhisperModelDirectory, true,
                        new InlineProgress<long>(bytes => whisperSetupStatus =
                            $"Downloading the Whisper transcription model: {bytes * 100 / model.Bytes}% ({bytes / 1048576:N0} / {totalMiB:N0} MiB). " +
                            "Queued Whisper audio is transcribed as soon as it finishes."), token);
                    if (WhisperModelPath is null) UpdateSettings(current => current with { WhisperModelPath = path });
                    Notify("Whisper large-v3-turbo downloaded and verified. Queued Whisper audio is being transcribed.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error)
                {
                    whisperSetupFailed = true;
                    Notify("The Whisper model could not be downloaded automatically (" + error.Message +
                        "). Install it from Privacy / models; queued Whisper audio is transcribed afterward.", true);
                }
                finally { whisperSetupStatus = null; }
                Store.ReleaseProviderJobs("local-whisper");
                wake.Release();
            });
            return true;
        }
    }

    private void StartLocalGpu()
    {
        lock (setupGate)
        {
            if (localGpuStart is { IsCompleted: false } || localGpu is not null) return;
            localGpuStart = Task.Run(() => StartLocalGpuAsync(shutdown.Token));
        }
    }

    /// <summary>
    /// Checks this PC for an NVIDIA GPU that NVIDIA's Parakeet container can use. The container runs only when the user opts in
    /// ("LocalGpuParakeet": true): on Windows it supports only Parakeet CTC, which tested less accurate than CPU Parakeet TDT.
    /// </summary>
    private async Task StartLocalGpuAsync(CancellationToken token)
    {
        if (providerOverride is not null) return;
        try
        {
            var gpus = await GpuProbe.QueryNvidiaGpusAsync(token);
            if (LocalNimCatalog.Select(gpus) is not { } choice)
            {
                localGpuStatus = gpus.Count == 0
                    ? "No NVIDIA GPU was found. Parakeet runs on the CPU, which is fast and the most accurate local option."
                    : string.Join(", ", gpus.Select(gpu => $"{gpu.Name} ({gpu.MemoryMiB / 1024.0:0.#} GB, compute {gpu.ComputeCapability:0.0})")) +
                      " can't run NVIDIA's Parakeet container (it needs an RTX 30-series or newer with 8 GB). Parakeet runs on the CPU.";
                return;
            }
            var (gpu, profile) = choice;
            if (!settings.LocalGpuParakeet)
            {
                localGpuStatus = $"{gpu.Name} can run NVIDIA's Parakeet container, but on Windows (Docker Desktop / WSL 2) NVIDIA supports only " +
                    "the CTC model there, which tested less accurate than Parakeet TDT on the CPU (7.8% vs 6.9% word errors). CPU Parakeet is used. " +
                    "To move Parakeet to the GPU anyway (less CPU load), set \"LocalGpuParakeet\": true in preferences.json and run Docker Desktop.";
                return;
            }
            var docker = LocalNimHost.FindDocker();
            var host = docker is null ? null : new LocalNimHost(docker);
            if (host is null || !await host.IsDockerRunningAsync(token))
            {
                localGpuStatus = $"{gpu.Name} can run Parakeet, but Docker Desktop is {(docker is null ? "not installed" : "not running")}. " +
                    "Start Docker Desktop (WSL 2 backend), then restart AudioTranscriber to use the GPU. CPU Parakeet is used meanwhile.";
                Notify(localGpuStatus);
                return;
            }
            using var key = await credentials.GetAsync(token);
            if (key is null)
            {
                localGpuStatus = $"{gpu.Name} can run Parakeet. Enter an NVIDIA key in Privacy / models so NVIDIA's container can be downloaded; CPU Parakeet is used meanwhile.";
                return;
            }
            localGpuStatus = localGpuSetupStatus = $"Starting {profile.DisplayName} on {gpu.Name}…";
            nimHost = host;
            await host.StartAsync(profile, gpu, key, new InlineProgress<string>(message =>
                localGpuStatus = localGpuSetupStatus = message + " Transcription keeps working on the CPU meanwhile."), token);
            localGpu = new LocalRivaProvider(profile);
            localGpuStatus = $"{profile.DisplayName} is running on {gpu.Name}: English Parakeet work uses the GPU, with no upload.";
            Notify(localGpuStatus);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException or
            HttpRequestException or System.ComponentModel.Win32Exception)
        {
            localGpuStatus = "Parakeet could not start on the GPU (" + error.Message + "). CPU Parakeet is used instead.";
            Notify(localGpuStatus, true);
        }
        finally { localGpuSetupStatus = null; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public string? WhisperModelPath => settings.WhisperModelPath is { } path && File.Exists(path) ? path : null;
    private string WhisperModelDirectory => Path.Combine(Path.GetDirectoryName(modelDirectory)!, "whisper");

    public Task InstallRecommendedWhisperModelAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        InstallWhisperModelAsync(LocalWhisperModelCatalog.Recommended.Id, progress, cancellationToken);

    public async Task InstallWhisperModelAsync(string modelId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var model = LocalWhisperModelCatalog.All.SingleOrDefault(item => item.Id == modelId)
            ?? throw new ArgumentException("Unknown Whisper model. Choose one of: " + string.Join(", ", LocalWhisperModelCatalog.All.Select(item => item.Id)));
        var path = await VerifiedModelDownload.InstallWhisperAsync(model, WhisperModelDirectory, true,
            new Progress<long>(bytes => progress?.Report($"Downloading {model.FileName}: {bytes * 100 / model.Bytes}% ({bytes / 1048576:N0} / {model.Bytes / 1048576:N0} MiB)")),
            cancellationToken);
        SetLocalWhisperModel(path);
        whisperSetupFailed = false;
        progress?.Report($"{model.FileName} installed, verified, and selected.");
    }

    public async Task InstallDiarizationModelsAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await DiarizationModels.InstallAsync(modelDirectory,
            new Progress<long>(bytes => progress?.Report($"Downloading/verifying diarization artifact: {bytes:N0} bytes")), cancellationToken);
        progress?.Report("Segmentation and speaker embedding models are installed and hash-verified.");
        Store.ReleaseProviderJobs(DiarizationProvider);
        wake.Release();
        Notify("Speaker models are ready. Speaker analysis that was waiting for them continues automatically.");
    }

    public async Task<MediaProbeSummary> ProbeMediaAsync(string path, CancellationToken cancellationToken = default)
    {
        var probe = await media.ProbeAsync(path, cancellationToken);
        return new(probe.Path, probe.DurationSeconds ?? 0, probe.AudioStreams.Select(stream =>
            new MediaStreamChoice(stream.Index,
                $"Stream {stream.Index}: {stream.Codec}, {stream.Format?.SampleRate} Hz, {stream.Format?.Channels} channel(s)")).ToArray());
    }

    public async Task<StoredSession> StartRecordingAsync(string name, string outputDeviceId, string? microphoneDeviceId,
        string providerId, string language, bool cloudConsent, bool reduceEcho = true, CancellationToken cancellationToken = default)
    {
        await captureGate.WaitAsync(cancellationToken);
        try
        {
            if (IsRecording) throw new InvalidOperationException("Stop the current recording before starting another.");
            RequireProvider(providerId);
            if (providerId == "local-whisper") StartWhisperDownload();
            var session = Store.CreateSession(name, providerId, language);
            var outputId = Guid.NewGuid();
            microphoneTrackId = microphoneDeviceId is null ? null : Guid.NewGuid();
            Store.AddTrack(new(outputId, session.Id, "Loopback", "Windows output", null, 0, null));
            if (microphoneTrackId is { } mic)
                Store.AddTrack(new(mic, session.Id, "Microphone", "Local microphone", null, 0,
                    reduceEcho ? JsonSerializer.Serialize(new MicrophoneOptions(outputId)) : null));
            Store.SetConsent(session.Id, cloudConsent);
            Store.SetSessionState(session.Id, "Starting");
            RecordingSessionId = session.Id;
            captureFaulted = false;
            recordingClock.Restart();
            StartCaptureFeed(session, outputId);
            if (microphoneTrackId is { } microphone) StartCaptureFeed(session, microphone);
            try
            {
                // Short, pause-aligned chunks keep speech-to-text latency near one phrase instead of 30 seconds.
                await capture.StartAsync(new(session.Id, Path.Combine(session.Directory, "originals"), outputDeviceId,
                    microphoneDeviceId, outputId, microphoneTrackId, ChunkDurationSeconds: LiveChunkMaxSeconds,
                    PauseSplitAfterMilliseconds: LivePauseSplitAfterMilliseconds), cancellationToken);
                Store.SetSessionState(session.Id, "Recording");
                Notify("Recording selected Windows output" + (microphoneDeviceId is null ? ". Local microphone is not captured." : " and a separate microphone track."));
            }
            catch
            {
                try { await capture.StopAsync(CancellationToken.None); }
                finally
                {
                    recordingClock.Stop();
                    foreach (var feed in captureFeeds.Values) feed.Signals.Writer.TryComplete();
                    try { await Task.WhenAll(captureFeeds.Values.Select(feed => feed.Task)); }
                    finally
                    {
                        captureFeeds.Clear();
                        RecordingSessionId = null;
                        UpdateCaptureCheckpoint(() => Store.SetSessionDuration(session.Id, recordingClock.Elapsed.Ticks));
                        UpdateCaptureCheckpoint(() => Store.SetSessionState(session.Id, "Faulted",
                            "Capture could not start. Check the selected endpoint and Windows microphone permissions."));
                    }
                }
                throw;
            }
            return Store.GetSession(session.Id);
        }
        finally { captureGate.Release(); }
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        await captureGate.WaitAsync(cancellationToken);
        try
        {
            if (RecordingSessionId is not { } sessionId) return;
            UpdateCaptureCheckpoint(() => Store.SetSessionState(sessionId, "Stopping"));
            Notify("Stopping capture; flushing original audio and final normalization output.");
            try
            {
                try { await capture.StopAsync(CancellationToken.None); }
                catch (Exception error) when (IsOperational(error))
                {
                    captureFaulted = true;
                    Notify("Capture stop reported an error: " + error.Message, true);
                    throw;
                }
                finally
                {
                    recordingClock.Stop();
                    foreach (var feed in captureFeeds.Values) feed.Signals.Writer.TryComplete();
                    await Task.WhenAll(captureFeeds.Values.Select(feed => feed.Task));
                    UpdateCaptureCheckpoint(() => Store.SetSessionDuration(sessionId, recordingClock.Elapsed.Ticks));
                    UpdateCaptureCheckpoint(() => Store.SetSessionState(sessionId, captureFaulted ? "Recoverable" : "Recorded",
                        captureFaulted ? "Capture or indexing reported an error. Original chunks and gap diagnostics are retained." : null));
                }
            }
            finally
            {
                captureFeeds.Clear();
                RecordingSessionId = null;
                microphoneTrackId = null;
                outputLevel = microphoneLevel = 0;
                LevelsChanged?.Invoke(new(0, 0));
            }
            wake.Release();
            Notify(captureFaulted ? "Capture stopped after errors. Review retained originals and recovery diagnostics."
                : "Recording stopped. Durable transcription jobs continue; Stop did not discard ASR responses.", captureFaulted);
        }
        finally { captureGate.Release(); }
    }

    public async Task<StoredSession> ImportAudioAsync(string name, string path, int streamIndex, string providerId,
        string language, bool cloudConsent, CancellationToken cancellationToken = default)
    {
        RequireProvider(providerId);
        if (providerId == "local-whisper") StartWhisperDownload();
        var session = Store.CreateSession(name, providerId, language);
        var track = new StoredTrack(Guid.NewGuid(), session.Id, "Imported", Path.GetFileName(path), null, streamIndex,
            JsonSerializer.Serialize(new MediaCheckpoint(Path.GetFullPath(path), streamIndex, null)));
        Store.AddTrack(track);
        Store.SetConsent(session.Id, cloudConsent);
        await RunMediaAsync(session, token => NormalizeImportedTrackAsync(session, track, token), cancellationToken);
        return Store.GetSession(session.Id);
    }

    public async Task ImportVttAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        var count = await TranscriptImports.ImportWebVttAsync(Store, sessionId, path, cancellationToken);
        Notify($"Imported {count:N0} source-timed WebVTT cues. Alignment to session audio is not independently verified.");
    }

    public async Task FetchTeamsTranscriptAsync(Guid sessionId, TeamsTranscriptRequest request,
        Func<DeviceSignInPrompt, Task> showSignIn, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Store.GetSession(sessionId).Directory, "teams");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".vtt");
        await new TeamsTranscriptClient().DownloadAsync(request, path, showSignIn, cancellationToken);
        var count = await TranscriptImports.ImportWebVttAsync(Store, sessionId, path, cancellationToken,
            allowVoiceLabels: !request.Unattributed);
        Notify($"Imported {count:N0} Teams transcript cues. Attribution policy was respected; this is not live Teams audio capture.");
    }

    public async Task DiarizeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!DiarizationModelsReady) throw new InvalidOperationException("Install the disclosed 33.49 MB local diarization models first.");
        if (Store.GetSession(sessionId).ProcessingState != "Running")
            throw new InvalidOperationException("Session processing is suspended. Resume it before running speaker analysis.");
        await Task.Run(() =>
        {
            QueueDiarization(sessionId);
            foreach (var track in Store.GetTracks(sessionId))
                foreach (var chunk in Store.GetChunks(track.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RefreshSpeakerAssignments(sessionId, track.Id, chunk.StartTicks,
                        chunk.StartTicks + chunk.SampleCount * TimeSpan.TicksPerSecond / 16000L);
                }
            Store.ResumeProviderJobs(sessionId, DiarizationProvider);
            // Re-run finished windows too: voice profiles learned since then (names, merges, labeled lines) can resolve old unknowns.
            RequeueSpeakerWindows(sessionId, onlyUnresolved: false);
        }, cancellationToken);
        wake.Release();
        Notify("Speaker analysis queued: every window is re-checked against the current voice profiles. Short turns and overlap can remain unknown.");
    }

    public StoredSpeaker GetOrCreateSpeaker(Guid sessionId, string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("A speaker name is required.");
        return Store.GetSpeakers(sessionId).FirstOrDefault(speaker => SameName(speaker.Name, name))
            ?? Store.CreateSpeaker(sessionId, name);
    }

    public void AssignSpeaker(Guid sessionId, IReadOnlyCollection<string> segmentIds, string? speakerId)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0) return;
        var speaker = speakerId is null ? null : Store.GetSpeakers(sessionId).FirstOrDefault(item => item.Id == speakerId)
            ?? throw new ArgumentException("Choose a speaker from this session.");
        Store.AssignSpeaker(sessionId, segmentIds, speakerId);
        TranscriptChanged?.Invoke(sessionId);
        if (speaker is not null && Guid.TryParse(speaker.Id, out var speakerGuid) && DiarizationModelsReady)
        {
            var ids = segmentIds.ToArray();
            StartBackground(token => LearnVoiceAsync(sessionId, speaker, speakerGuid, ids, token));
        }
    }

    public async Task<string> RenameSpeakerAsync(Guid sessionId, string speakerId, string name, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("A speaker name is required.");
        string keep;
        StoredSpeaker existing;
        await registryGate.WaitAsync(cancellationToken);
        try
        {
            var speakers = Store.GetSpeakers(sessionId);
            var source = speakers.FirstOrDefault(item => item.Id == speakerId) ?? throw new ArgumentException("Choose a speaker from this session.");
            var match = speakers.FirstOrDefault(item => item.Id != speakerId && SameName(item.Name, name));
            if (match is null)
            {
                Store.RenameSpeaker(sessionId, speakerId, name);
                TranscriptChanged?.Invoke(sessionId);
                return speakerId;
            }
            existing = match;
            keep = MergeLocked(sessionId, source, existing);
        }
        finally { registryGate.Release(); }
        TranscriptChanged?.Invoke(sessionId);
        var requeued = RequeueSpeakerWindows(sessionId, onlyUnresolved: true);
        Notify($"Merged into \"{existing.Name.Trim()}\": their voice samples are combined, so future lines match either voice." +
            (requeued > 0 ? $" Re-checking {requeued} window(s) that still have unknown speakers." : ""));
        return keep;
    }

    // Same name means same person: fold both voice profiles together so future lines match either one.
    // Keeps the existing speaker's name; keeps whichever ID has a voice profile. Caller holds registryGate.
    private string MergeLocked(Guid sessionId, StoredSpeaker source, StoredSpeaker existing)
    {
        var json = Store.GetSpeakerRegistry(sessionId);
        var registry = json is null ? null : CoreRegistrySerializer.Deserialize(json);
        bool HasProfile(string id) => registry is not null && Guid.TryParse(id, out var guid) &&
            registry.Speakers.Any(entry => entry.Identity.Id == guid && entry.Identity.MergedIntoId is null);
        var (keep, drop) = HasProfile(existing.Id) || !HasProfile(source.Id) ? (existing.Id, source.Id) : (source.Id, existing.Id);
        string? updated = null;
        if (HasProfile(drop))
        {
            var into = Guid.Parse(keep);
            var from = Guid.Parse(drop);
            updated = CoreRegistrySerializer.Serialize(registry! with
            {
                Revision = registry.Revision + 1,
                Speakers = registry.Speakers.Select(entry => entry.Identity.Id == from
                    ? entry with { Identity = entry.Identity with { MergedIntoId = into } } : entry).ToImmutableArray()
            });
        }
        Store.MergeSpeakers(sessionId, drop, keep, existing.Name.Trim(), updated);
        return keep;
    }

    private static bool IsAutomaticName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^Speaker \d+$");

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private void StartBackground(Func<CancellationToken, Task> work)
    {
        var id = Guid.NewGuid();
        var task = Task.Run(async () =>
        {
            try { await work(shutdown.Token); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception error) when (IsOperational(error)) { Notify("Learning the speaker's voice failed: " + error.Message, true); }
            finally { backgroundTasks.TryRemove(id, out _); }
        });
        backgroundTasks[id] = task;
        if (task.IsCompleted) backgroundTasks.TryRemove(id, out _);
    }

    private const int MaximumEnrollmentSamples = 60 * 16000;

    // Embeds the user-labeled lines' audio into that speaker's voice profile, then re-checks unresolved windows.
    private async Task LearnVoiceAsync(Guid sessionId, StoredSpeaker speaker, Guid speakerGuid, string[] segmentIds, CancellationToken token)
    {
        var rows = Store.GetSegments(sessionId, segmentIds)
            .Where(row => !row.Provenance.StartsWith("WebVTT ", StringComparison.Ordinal) && row.EndTicks - row.StartTicks >= TimeSpan.TicksPerSecond)
            .ToArray();
        if (rows.Length == 0) return;
        var workPath = Path.Combine(Store.GetSession(sessionId).Directory, "work", $"enroll-{Guid.NewGuid():N}.pcm16");
        Directory.CreateDirectory(Path.GetDirectoryName(workPath)!);
        try
        {
            long samples = 0;
            var used = 0;
            await using (var output = new FileStream(workPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, true))
            {
                var chunks = new Dictionary<Guid, IReadOnlyList<StoredAudioChunk>>();
                foreach (var row in rows)
                {
                    const int gap = 16000 / 4;
                    if (samples + gap + 16000 > MaximumEnrollmentSamples) break;
                    if (!chunks.TryGetValue(row.TrackId, out var trackChunks)) chunks[row.TrackId] = trackChunks = Store.GetChunks(row.TrackId);
                    var separator = samples == 0 ? 0 : gap;
                    var added = await AppendRowAudioAsync(output, trackChunks, row.StartTicks, row.EndTicks,
                        MaximumEnrollmentSamples - samples - separator, separator, token);
                    if (added == 0) continue;
                    samples += separator + added;
                    used++;
                }
            }
            if (samples < 16000 * 3 / 2)
            {
                Notify($"The labeled line(s) are too short to learn {speaker.Name}'s voice. Labeling a longer line teaches it.");
                return;
            }
            string? absorbed = null, soundsLike = null;
            await registryGate.WaitAsync(token);
            try
            {
                var registry = LoadRegistry(sessionId, out _);
                await using var service = CreateDiarizer(sessionId);
                if (service is not ISpeakerEnrollmentService enrollment) return;
                var result = await enrollment.EnrollAsync(new(sessionId, rows[0].TrackId, workPath, samples, rows[0].StartTicks),
                    registry, new(speakerGuid, speaker.Name), token);
                if (!result.Diagnostics.Any(item => item.StartsWith("Enrolled:", StringComparison.Ordinal)))
                {
                    Notify($"Not enough clear speech in the labeled line(s) to learn {speaker.Name}'s voice yet. Labeling a longer line teaches it.");
                    return;
                }
                Store.SetSpeakerRegistry(sessionId, CoreRegistrySerializer.Serialize(result.Registry));
                // The voice already belongs to another profile. An automatic "Speaker N" is the same person under a
                // placeholder name, so fold it in; a speaker the user named is only pointed out.
                var similarId = result.Diagnostics.Where(item => item.StartsWith("SimilarTo:", StringComparison.Ordinal))
                    .Select(item => item.Split(':')[1]).FirstOrDefault();
                var current = Store.GetSpeakers(sessionId);
                if (similarId is not null && current.FirstOrDefault(item => item.Id == similarId) is { } similar &&
                    current.FirstOrDefault(item => item.Id == speaker.Id) is { } labeled)
                {
                    if (IsAutomaticName(similar.Name) && !IsAutomaticName(labeled.Name)) { MergeLocked(sessionId, similar, labeled); absorbed = similar.Name; }
                    else soundsLike = similar.Name;
                }
            }
            finally { registryGate.Release(); }
            if (absorbed is not null) TranscriptChanged?.Invoke(sessionId);
            var requeued = RequeueSpeakerWindows(sessionId, onlyUnresolved: true);
            Notify($"Learned {speaker.Name}'s voice from {used} labeled line(s)." +
                (absorbed is not null ? $" It matches {absorbed}, so {absorbed}'s lines are now {speaker.Name}." : "") +
                (soundsLike is not null ? $" It sounds like {soundsLike}; if they are the same person, rename {soundsLike} to {speaker.Name} to merge them." : "") +
                (requeued > 0 ? $" Re-checking {requeued} window(s) that still have unknown speakers." : ""));
        }
        finally { if (File.Exists(workPath)) File.Delete(workPath); }
    }

    private static async Task<long> AppendRowAudioAsync(Stream output, IReadOnlyList<StoredAudioChunk> chunks, long startTicks, long endTicks,
        long maximumSamples, int leadingSilence, CancellationToken token)
    {
        long written = 0;
        foreach (var chunk in chunks)
        {
            var chunkEnd = chunk.StartTicks + chunk.SampleCount * TimeSpan.TicksPerSecond / 16000L;
            if (chunkEnd <= startTicks || chunk.StartTicks >= endTicks) continue;
            var from = Math.Max(0, (startTicks - chunk.StartTicks) * 16000 / TimeSpan.TicksPerSecond);
            var to = Math.Min(chunk.SampleCount, (endTicks - chunk.StartTicks) * 16000 / TimeSpan.TicksPerSecond);
            var count = Math.Min(to - from, maximumSamples - written);
            if (count <= 0) continue;
            if (written == 0 && leadingSilence > 0) await output.WriteAsync(new byte[leadingSilence * 2], token);
            await using var input = new FileStream(chunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, true);
            if (input.Length != chunk.SampleCount * 2L)
                throw new InvalidDataException("A normalized chunk does not match its committed PCM16 sample count.");
            input.Position = from * 2;
            var buffer = new byte[count * 2];
            await input.ReadExactlyAsync(buffer, token);
            await output.WriteAsync(buffer, token);
            written += count;
            if (written >= maximumSamples) break;
        }
        return written;
    }

    // Queues finished speaker-analysis windows to run again with the current registry.
    private int RequeueSpeakerWindows(Guid sessionId, bool onlyUnresolved)
    {
        if (!DiarizationModelsReady) return 0;
        var jobs = Store.GetProviderJobs(sessionId, DiarizationProvider);
        var jobChunks = jobs.Select(job => job.ChunkId).ToHashSet();
        var selected = new List<Guid>();
        foreach (var group in jobs.Where(job => job.State == "Succeeded").GroupBy(job => job.TrackId))
        {
            if (!onlyUnresolved) { selected.AddRange(group.Select(job => job.Id)); continue; }
            var chunks = Store.GetChunks(group.Key);
            var index = new Dictionary<Guid, int>();
            for (var i = 0; i < chunks.Count; i++) index[chunks[i].Id] = i;
            foreach (var job in group)
            {
                if (!index.TryGetValue(job.ChunkId, out var last)) continue;
                var first = last;
                long total = chunks[last].SampleCount;
                while (first > 0 && total + chunks[first - 1].SampleCount <= Pcm16Audio.MaximumSamples &&
                       RecognitionWindowBuilder.Adjacent(chunks[first - 1], chunks[first]) && !jobChunks.Contains(chunks[first - 1].Id))
                    total += chunks[--first].SampleCount;
                var end = chunks[last].StartTicks + chunks[last].SampleCount * TimeSpan.TicksPerSecond / 16000L;
                if (Store.GetTurns(group.Key, chunks[first].StartTicks, end).Any(turn => turn.SpeakerId is null)) selected.Add(job.Id);
            }
        }
        var count = selected.Count == 0 ? 0 : Store.RequeueJobs(selected);
        if (count > 0) wake.Release();
        return count;
    }

    public void PauseTranscription(Guid sessionId)
    {
        Store.PauseJobs(sessionId);
        CancelActiveJobs(lane => lane.Session == sessionId);
        Notify("Transcription paused; recording and retained originals are unaffected.");
    }

    public void ResumeTranscription(Guid sessionId)
    {
        var session = Store.GetSession(sessionId);
        if (settings.CloudBlockReason is not null && session.CloudConsent && RequireProvider(session.ProviderId).IsCloud)
            UpdateSettings(current => current with { CloudBlockReason = null });
        Store.ResumeJobs(sessionId);
        if (session.State is "Recoverable" or "Importing" or "Faulted" && !mediaTasks.ContainsKey(sessionId) &&
            RecordingSessionId != sessionId)
        {
            _ = ObserveRecoveryAsync(session);
        }
        wake.Release();
        Notify("Durable work resumed. Cloud jobs still require the session's explicit upload consent.");
    }

    public void CancelTranscription(Guid sessionId)
    {
        Store.CancelJobs(sessionId);
        CancelActiveJobs(lane => lane.Session == sessionId);
        if (mediaCancellation.TryGetValue(sessionId, out var cancellation)) cancellation.Cancel();
        Notify("Transcription/import work canceled. Existing source audio, chunks, and completed results are retained.");
    }

    public void SetCloudConsent(Guid sessionId, bool consent)
    {
        Store.SetConsent(sessionId, consent);
        if (!consent) CancelActiveJobs(lane => lane.Session == sessionId && lane.Cloud);
        wake.Release();
        Notify(consent ? "This session permits NVIDIA submission of its recorded/imported audio tracks."
            : "Future NVIDIA uploads are disabled. Audio already sent cannot be recalled.");
    }

    public async Task PlayAsync(Guid sessionId, Guid trackId, long sessionTicks, CancellationToken cancellationToken = default)
    {
        var track = Store.GetTracks(sessionId).Single(item => item.Id == trackId);
        if (track.Kind == "Transcript")
            throw new InvalidOperationException("This transcript has no linked recording to play.");
        if (track.Kind == "Imported")
        {
            var checkpoint = ReadImport(track);
            var imported = checkpoint.Imported ?? throw new InvalidOperationException("The original media copy is not complete.");
            var format = imported.Probe.AudioStreams.Single(stream => stream.Index == imported.AudioStreamIndex).Format;
            var maximum = Math.Max(TimeSpan.TicksPerSecond, Store.GetSession(sessionId).DurationTicks - sessionTicks);
            if (timelinePlayer is not null)
            {
                await timelinePlayer.PlayAsync(new AudioTranscriber.Audio.PlaybackRequest(sessionTicks,
                    [new(trackId, Clips: [new(imported.ManagedOriginalPath, imported.AudioStreamIndex, 0,
                        format?.SampleRate ?? 16000, 0, null)])]), cancellationToken);
                return;
            }
            await playback.PlayAsync(new(new(trackId, imported.ManagedOriginalPath, format, 0,
                AudioStreamIndex: imported.AudioStreamIndex), sessionTicks, maximum), cancellationToken);
            return;
        }
        var chunks = Store.GetArchiveManifests(trackId).Select(DeserializeNative).OrderBy(chunk => chunk.SessionStartTicks).ToArray();
        var selected = chunks.FirstOrDefault(chunk => chunk.SessionStartTicks <= sessionTicks && chunk.SessionEndTicks > sessionTicks)
            ?? throw new InvalidOperationException("No original audio is retained at this timestamp; it may lie in a recorded gap.");
        if (timelinePlayer is not null)
        {
            await timelinePlayer.PlayAsync(new AudioTranscriber.Audio.PlaybackRequest(sessionTicks,
                [new(trackId, Clips: chunks.Select(chunk => new PlaybackClip(chunk.Path, 0,
                    chunk.SessionStartTicks, chunk.Format.SampleRate, 0, chunk.SourceFrameCount)).ToArray())]), cancellationToken);
            return;
        }
        await playback.PlayAsync(new(new(trackId, selected.Path, selected.Format, selected.SessionStartTicks,
            selected.SourceFrameOffset, selected.SourceFrameCount), sessionTicks, selected.SessionEndTicks - sessionTicks), cancellationToken);
    }

    public void StopPlayback() => Task.Run(async () =>
    {
        await playback.StopAsync();
        if (timelinePlayer is not null) await timelinePlayer.StopAsync();
    }).GetAwaiter().GetResult();

    private void StartCaptureFeed(StoredSession session, Guid trackId)
    {
        // This channel coalesces notifications only. The SQLite/archive manifest is the queue of audio.
        var feed = new CaptureFeed();
        captureFeeds[trackId] = feed;
        feed.Task = Task.Run(async () =>
        {
            try
            {
                await ConsumeNormalizedAsync(session, media.NormalizeAsync(ObserveOriginals(trackId, feed, shutdown.Token),
                    LiveNormalization(session, trackId), shutdown.Token), shutdown.Token);
            }
            catch (Exception error) when (IsOperational(error))
            {
                captureFaulted = true;
                Notify("Normalization paused; originals remain recorded. " + error.Message, true);
            }
        });
    }

    private async IAsyncEnumerable<Core.NativeChunk> ObserveOriginals(Guid trackId, CaptureFeed feed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long nextFrame = 0;
        while (true)
        {
            foreach (var chunk in Store.GetArchiveManifests(trackId).Select(DeserializeNative).OrderBy(chunk => chunk.SourceFrameOffset))
            {
                if (chunk.SourceFrameOffset < nextFrame) continue;
                yield return chunk;
                nextFrame = chunk.SourceEndFrame;
            }
            if (!await feed.Signals.Reader.WaitToReadAsync(cancellationToken)) break;
            while (feed.Signals.Reader.TryRead(out _)) { }
        }
        foreach (var chunk in Store.GetArchiveManifests(trackId).Select(DeserializeNative).OrderBy(chunk => chunk.SourceFrameOffset))
            if (chunk.SourceFrameOffset >= nextFrame) { yield return chunk; nextFrame = chunk.SourceEndFrame; }
    }

    private void OnNativeChunk(Core.NativeChunk chunk)
    {
        try
        {
            Store.AddArchiveChunk(chunk.Id, chunk.TrackId, chunk.Path, chunk.SourceFrameOffset,
                chunk.SourceFrameCount, chunk.SessionStartTicks, JsonSerializer.Serialize(chunk));
            if (captureFeeds.TryGetValue(chunk.TrackId, out var feed)) feed.Signals.Writer.TryWrite(1);
        }
        catch (Exception error) when (IsOperational(error))
        {
            captureFaulted = true;
            Notify("Original audio was sealed, but database indexing failed; recovery is required. " + error.Message, true);
        }
    }

    private void OnLevels(Core.AudioLevels levels)
    {
        if (levels.TrackId == microphoneTrackId) microphoneLevel = levels.Peak;
        else outputLevel = levels.Peak;
        LevelsChanged?.Invoke(new(outputLevel, microphoneLevel));
    }

    private void OnCaptureFault(Core.CaptureFault fault)
    {
        captureFaulted = true;
        Notify($"Recording fault [{fault.Code}]: {fault.Message}. Review retained original chunks and gap diagnostics.", true);
        _ = StopAfterCaptureFaultAsync();
    }

    private void UpdateCaptureCheckpoint(Action update)
    {
        try { update(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            captureFaulted = true;
            Notify("Capture checkpoint failed; stopping audio must not depend on database writes. " + error.Message, true);
        }
    }

    private async Task StopAfterCaptureFaultAsync()
    {
        try { await StopRecordingAsync(CancellationToken.None); }
        catch (Exception error) when (IsOperational(error))
        {
            Notify("Capture fault cleanup needs attention; original files were retained: " + error.Message, true);
        }
    }

    private async Task RunMediaAsync(StoredSession session, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        if (!mediaCancellation.TryAdd(session.Id, linked))
            throw new InvalidOperationException("This session already has an import/recovery operation running.");
        try
        {
            Store.SetSessionState(session.Id, "Importing");
            var task = Task.Run(() => operation(linked.Token), CancellationToken.None);
            mediaTasks[session.Id] = task;
            await task;
            Store.SetSessionState(session.Id, "Recorded");
            Notify("Original media retained and final normalization output sealed. Queued recognition continues independently.");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            SetMediaRecoveryState(session.Id, "Media processing canceled. Resume replays the retained source and reuses verified completed chunks.");
            Notify("Media processing canceled; retained originals and completed chunks remain available.");
            throw;
        }
        catch (Exception error) when (IsOperational(error))
        {
            SetMediaRecoveryState(session.Id, error.Message);
            Notify("Media processing needs attention: " + error.Message, true);
            throw;
        }
        finally
        {
            mediaTasks.TryRemove(session.Id, out _);
            mediaCancellation.TryRemove(session.Id, out _);
        }
    }

    private void SetMediaRecoveryState(Guid sessionId, string error)
    {
        try { Store.SetSessionState(sessionId, "Recoverable", error); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Notify("Media recovery could not update its database checkpoint: " + failure.Message, true);
        }
    }

    private async Task ObserveRecoveryAsync(StoredSession session)
    {
        try { await RunMediaAsync(session, token => RecoverAndNormalizeAsync(session, token), CancellationToken.None); }
        catch (Exception error) when (IsOperational(error) || error is OperationCanceledException)
        {
            Notify("Recovery did not finish; its saved checkpoint remains resumable: " + error.Message, true);
        }
    }

    private async Task NormalizeImportedTrackAsync(StoredSession session, StoredTrack track, CancellationToken cancellationToken)
    {
        var checkpoint = ReadImport(track);
        var imported = checkpoint.Imported ?? await RecoverImportedCopyAsync(session, track, cancellationToken);
        if (imported is null)
        {
            imported = await media.CopyImportAsync(new(session.Id, track.Id, checkpoint.SourcePath, checkpoint.StreamIndex,
                Path.Combine(session.Directory, "imports")), new Progress<OperationProgress>(progress =>
                Notify($"{progress.Stage}: {progress.Completed:N0}" + (progress.Total is { } total ? $" / {total:N0}" : ""))), cancellationToken);
            Store.UpdateTrackSource(track.Id, imported.ManagedOriginalPath, imported.AudioStreamIndex,
                JsonSerializer.Serialize(checkpoint with { Imported = imported }));
        }
        else if (checkpoint.Imported is null)
            Store.UpdateTrackSource(track.Id, imported.ManagedOriginalPath, imported.AudioStreamIndex,
                JsonSerializer.Serialize(checkpoint with { Imported = imported }));
        await ConsumeNormalizedAsync(session, media.NormalizeImportAsync(imported,
            new(Path.Combine(session.Directory, "normalized", track.Id.ToString("N"))), cancellationToken), cancellationToken);
    }

    private async Task<ImportedMedia?> RecoverImportedCopyAsync(StoredSession session, StoredTrack track,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(session.Directory, "imports");
        if (!Directory.Exists(directory)) return null;
        await Task.Run(() => MediaImporter.Recover(directory), cancellationToken);
        var candidates = new List<ManagedImport>();
        foreach (var manifest in Directory.EnumerateFiles(directory, "*.import.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(manifest).Length > 1024 * 1024)
                throw new InvalidDataException("An import recovery manifest exceeds its size limit.");
            var candidate = JsonSerializer.Deserialize<ManagedImport>(await File.ReadAllTextAsync(manifest, cancellationToken))
                ?? throw new InvalidDataException("An import recovery manifest is invalid.");
            if (candidate.SessionId != session.Id || candidate.TrackId != track.Id) continue;
            if (!Path.GetFullPath(candidate.ManagedPath + ".import.json").Equals(Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An import manifest does not identify its retained file.");
            if (candidate.Stream.Index != track.AudioStreamIndex)
                throw new InvalidDataException("The retained import has a different selected audio stream.");
            await using var file = File.OpenRead(candidate.ManagedPath);
            if (file.Length != candidate.ByteLength ||
                !Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A retained import failed length or hash verification.");
            candidates.Add(candidate);
        }
        if (candidates.Count == 0) return null;
        if (candidates.Select(item => item.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new InvalidDataException("Conflicting retained originals need explicit recovery; no source was recopied.");
        var retained = candidates.OrderBy(item => item.ImportedAt).First();
        var probe = await media.ProbeAsync(retained.ManagedPath, cancellationToken);
        Notify("Recovered the verified managed original without recopying the external source.");
        return new(track.Id, retained.ManagedPath, retained.SourceName, retained.Sha256, retained.Stream.Index, probe, retained.ImportedAt);
    }

    private async Task RecoverAndNormalizeAsync(StoredSession session, CancellationToken cancellationToken)
    {
        var recovered = await Task.Run(() => AudioArchiveCatalog.RecoverOriginals(Path.Combine(session.Directory, "originals")), cancellationToken);
        foreach (var diagnostic in recovered.Diagnostics) Notify(diagnostic, true);
        foreach (var chunk in recovered.Chunks)
            OnNativeChunk(chunk.ToCore());
        foreach (var track in Store.GetTracks(session.Id))
        {
            if (track.Kind == "Imported")
                await NormalizeImportedTrackAsync(session, track, cancellationToken);
            else if (track.Kind is "Loopback" or "Microphone")
                await ConsumeNormalizedAsync(session, media.NormalizeAsync(RetainedOriginals(track.Id, cancellationToken),
                    LiveNormalization(session, track.Id), cancellationToken), cancellationToken);
        }
        if (recovered.Diagnostics.Count > 0)
            throw new InvalidDataException("Original recovery reported gaps or untrusted files; see the retained archive diagnostics.");
    }

    // Recorded tracks cut shards at capture-chunk boundaries (committed runs replay with their original layout).
    private static Core.NormalizationOptions LiveNormalization(StoredSession session, Guid trackId) =>
        new(Path.Combine(session.Directory, "normalized", trackId.ToString("N")), SealAtSourceChunks: true);

    private async IAsyncEnumerable<Core.NativeChunk> RetainedOriginals(Guid trackId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in Store.GetArchiveManifests(trackId).Select(DeserializeNative).OrderBy(chunk => chunk.SourceFrameOffset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
        await Task.CompletedTask;
    }

    private async Task ConsumeNormalizedAsync(StoredSession session, IAsyncEnumerable<NormalizedChunk> chunks, CancellationToken cancellationToken)
    {
        // Only word-timed providers read neighbor audio as context; others can transcribe each chunk as soon as it is sealed.
        var waitForNext = UsesNeighborContext(session.ProviderId);
        StoredAudioChunk? previous = null;
        long undiarized = 0;
        await foreach (var decoded in chunks.WithCancellation(cancellationToken))
        {
            var anchor = Store.GetArchiveManifests(decoded.TrackId).Select(DeserializeNative)
                .LastOrDefault(item => item.SourceFrameOffset <= decoded.SourceFrameOffset && item.SourceEndFrame > decoded.SourceFrameOffset);
            var chunk = anchor is null ? decoded : RecordedClockMapping.AtNativeAnchor(decoded, anchor);
            var stored = new StoredAudioChunk(chunk.Id, session.Id, chunk.TrackId, chunk.Path,
                chunk.NormalizedStartSample, checked((int)chunk.SampleCount), chunk.SessionStartTicks, JsonSerializer.Serialize(chunk));
            Store.AddNormalizedChunk(stored);
            undiarized = QueueDiarizationWindow(previous, stored, undiarized, session.Language);
            if (!waitForNext) QueueAsr(stored, session);
            else if (previous is not null) QueueAsr(previous, session);
            previous = stored;
            wake.Release();
            Notify($"Audio ready through {TimeSpan.FromTicks(chunk.SessionEndTicks):g}; recording/import is independent of recognition.");
        }
        if (previous is not null)
        {
            QueueAsr(previous, session);
            if (undiarized > 0) Store.QueueTranscription(previous, DiarizationProvider, session.Language, false);
        }
        wake.Release();
    }

    // Speaker analysis runs on ~20-second windows of consecutive chunks (queued on the window's last chunk):
    // short live chunks would otherwise each pay a full worker start and a padded 30-second inference.
    private const long DiarizationWindowSamples = 20 * 16000;

    private long QueueDiarizationWindow(StoredAudioChunk? previous, StoredAudioChunk current, long undiarized, string language)
    {
        if (previous is not null && undiarized > 0 && !RecognitionWindowBuilder.Adjacent(previous, current))
        {
            Store.QueueTranscription(previous, DiarizationProvider, language, false);
            undiarized = 0;
        }
        undiarized += current.SampleCount;
        if (undiarized < DiarizationWindowSamples) return undiarized;
        Store.QueueTranscription(current, DiarizationProvider, language, false);
        return 0;
    }

    private static bool UsesNeighborContext(string providerId) =>
        NvidiaModelCatalog.All.Any(model => model.Id == providerId && model.VerifiedWordTiming);

    private void QueueAsr(StoredAudioChunk chunk, StoredSession session) =>
        Store.QueueTranscription(chunk, session.ProviderId, session.Language, RequireProvider(session.ProviderId).IsCloud);

    private void QueueDiarization(Guid sessionId)
    {
        var session = Store.GetSession(sessionId);
        foreach (var track in Store.GetTracks(sessionId))
        {
            StoredAudioChunk? previous = null;
            long undiarized = 0;
            foreach (var chunk in Store.GetChunks(track.Id))
            {
                undiarized = QueueDiarizationWindow(previous, chunk, undiarized, session.Language);
                previous = chunk;
            }
            if (previous is not null && undiarized > 0) Store.QueueTranscription(previous, DiarizationProvider, session.Language, false);
        }
    }

    private void CancelActiveJobs(Func<JobLane, bool> predicate)
    {
        foreach (var lane in new[] { speechLane, speakerLane })
            if (predicate(lane)) lane.Cancellation?.Cancel();
    }

    private async Task SchedulerAsync(JobLane lane, string? provider, string? excludeProvider)
    {
        var signal = provider == DiarizationProvider ? wake.Speaker : wake.Speech;
        while (!shutdown.IsCancellationRequested && !stopping)
        {
            StoredJob? job;
            try { job = Store.ClaimNextJob(provider, excludeProvider); }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                Notify("The durable job queue is unavailable; capture retains its own originals. " + error.Message, true);
                try { await Task.Delay(TimeSpan.FromSeconds(5), shutdown.Token); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                continue;
            }
            if (job is null)
            {
                try { await signal.WaitAsync(TimeSpan.FromSeconds(2), shutdown.Token); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                continue;
            }
            lane.Session = job.SessionId;
            lane.Cloud = job.Cloud;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            lane.Cancellation = cancellation;
            try
            {
                lane.Task = ProcessJobAsync(job, cancellation.Token);
                await lane.Task;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Notify("The active job was interrupted; its durable source remains available.");
            }
            catch (Exception error) when (IsOperational(error))
            {
                Notify("Job bookkeeping failed; the scheduler will continue from durable checkpoints: " + error.Message, true);
                try { Store.FailJob(job, error.Message, "RetryWaiting", TimeSpan.FromSeconds(5)); }
                catch (Exception checkpointError) when (IsOperational(checkpointError))
                {
                    Notify("The job checkpoint is temporarily unavailable; lease recovery remains pending: " + checkpointError.Message, true);
                }
            }
            finally
            {
                lane.Task = null;
                lane.Cancellation = null;
                lane.Session = null;
                lane.Cloud = false;
            }
        }
    }

    private async Task ProcessJobAsync(StoredJob job, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = operation.Token;
        using var leaseLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLost = 0;
        var lease = RenewLeaseAsync(job, () => { Interlocked.Exchange(ref leaseLost, 1); operation.Cancel(); }, leaseLifetime.Token);
        string? workPath = null;
        try
        {
            if (job.Cloud && settings.CloudBlockReason is { } reason)
            {
                Store.FailJob(job, reason, "Blocked");
                return;
            }
            var chunk = Store.GetChunk(job.ChunkId);
            if (job.ProviderId == DiarizationProvider)
            {
                if (!DiarizationModelsReady)
                {
                    if (ModelSetupRunning)
                    {
                        Store.DeferJob(job, "Waiting for the speaker-labeling models to download.", TimeSpan.FromSeconds(30));
                        if (DiarizationModelsReady) Store.ReleaseProviderJobs(DiarizationProvider);
                        return;
                    }
                    Store.FailJob(job, "Install the local speaker-labeling models (Privacy / models) to assign automatic speakers.", "Blocked");
                    return;
                }
                await ProcessDiarizationAsync(job, chunk, cancellationToken);
                return;
            }
            if (job.ProviderId == "local-whisper" && providerOverride is null && WhisperModelPath is null && StartWhisperDownload())
            {
                Store.DeferJob(job, "Waiting for the Whisper model download to finish; transcription starts automatically.", TimeSpan.FromSeconds(60));
                if (WhisperModelPath is not null) Store.ReleaseProviderJobs("local-whisper");
                return;
            }
            // Opted-in GPU Parakeet (English only) takes over from the CPU model when it is running.
            var useGpu = job.ProviderId == SherpaParakeetProvider.ProviderId && localGpu is not null && LocalRivaProvider.SupportsLanguage(job.Language);
            if (job.ProviderId == SherpaParakeetProvider.ProviderId && providerOverride is null && !useGpu && !ParakeetModelReady)
            {
                if (modelSetup is { IsCompleted: false })
                {
                    Store.DeferJob(job, "Waiting for the Parakeet model download to finish; transcription starts automatically.", TimeSpan.FromSeconds(60));
                    if (ParakeetModelReady) Store.ReleaseProviderJobs(SherpaParakeetProvider.ProviderId);
                    return;
                }
                Store.FailJob(job, "The Parakeet model is not installed. Install it from Privacy / models (or choose Local Whisper); this audio is transcribed afterward.", "Blocked");
                return;
            }
            if (await IsSilentChunkAsync(chunk, cancellationToken))
            {
                var skipped = JsonSerializer.Serialize(new { Version = 1, Skipped = "silence", ChunkId = chunk.Id });
                Store.CompleteJob(job, [], skipped, "skipped:silence");
                return;
            }
            var provider = useGpu && localGpu is { } gpuProvider ? gpuProvider : CreateProvider(job.ProviderId);
            var allChunks = Store.GetChunks(job.TrackId);
            var index = allChunks.ToList().FindIndex(item => item.Id == chunk.Id);
            if (index < 0) throw new InvalidDataException("The queued audio chunk is not retained.");
            workPath = Path.Combine(Store.GetSession(job.SessionId).Directory, "work", job.Id.ToString("N") + ".pcm16");
            var window = await RecognitionWindowBuilder.CreateAsync(chunk, index > 0 ? allChunks[index - 1] : null,
                index + 1 < allChunks.Count ? allChunks[index + 1] : null, provider.Descriptor.Timing == TimingGranularity.Word,
                workPath, cancellationToken);
            var echoReduced = false;
            if (EchoReferenceTrack(job) is { } referenceTrackId)
            {
                var reference = Store.GetChunks(referenceTrackId);
                if (!EchoReferenceReady(job, window, reference, referenceTrackId))
                {
                    Store.DeferJob(job, "Waiting for the matching speaker audio to remove its echo from the microphone.", TimeSpan.FromSeconds(5));
                    return;
                }
                echoReduced = await ReduceEchoAsync(window, allChunks, reference, cancellationToken);
            }
            var source = JsonSerializer.Deserialize<NormalizedChunk>(chunk.MetadataJson)
                ?? throw new InvalidDataException("Normalized source timing metadata is missing.");
            var request = new TranscriptionRequest(job.SessionId, job.TrackId, job.ChunkId, window.Path, window.SampleCount,
                job.Language, window.SessionStartTicks, window.CoreStartSample, window.CoreSampleCount,
                checked(chunk.StartSample - window.CoreStartSample),
                checked(source.SourceFrameOffset - Core.AudioTime.Scale(window.CoreStartSample, source.SourceFormat.SampleRate, 16000)));
            var result = NormalizeResult(await provider.TranscribeAsync(request, cancellationToken));
            object? fallbackEvidence = null;
            string? fallbackNote = null;
            if (await RunConfidenceFallbackAsync(job, request, result, cancellationToken) is { } fallback)
            {
                var used = fallback.Result.Status != TranscriptionStatus.Partial;
                fallbackEvidence = new { fallback.Score, fallback.Threshold, Used = used, Local = result, Hosted = fallback.Result };
                if (used)
                {
                    provider = fallback.Provider;
                    result = fallback.Result;
                    fallbackNote = $"low-confidence fallback from local Whisper (score {fallback.Score:0.00} < {fallback.Threshold:0.00})";
                }
            }
            var evidence = JsonSerializer.Serialize(new { Version = 1, Request = request, Result = result, LowConfidenceFallback = fallbackEvidence });
            var modelIdentity = result.ObservedModel is { } observed ? "observed:" + observed : "advertised:" + provider.Descriptor.Model;
            Store.SaveRawAttempt(job, evidence, modelIdentity);
            if (result.Status == TranscriptionStatus.Partial)
                throw new TranscriptionProviderException(new(ProviderErrorCode.InvalidResponse,
                    "The provider returned partial audio coverage or invalid timing. Its raw response is retained; this chunk needs review or an explicit retry."));
            var provenance = $"{provider.Descriptor.Id}; advertised-model={provider.Descriptor.Model}; observed-model={result.ObservedModel ?? "not returned"}; " +
                $"track={job.TrackId:D}; normalized core={source.NormalizedStartSample}..{source.NormalizedEndSample} @16000Hz; " +
                $"native frames={source.SourceFrameOffset}..{source.SourceFrameOffset + source.SourceFrameCount} @{source.SourceFormat.SampleRate}Hz" +
                (fallbackNote is null ? "" : "; " + fallbackNote) +
                (echoReduced ? "; speaker echo removed (WebRTC AEC3)" : "");
            var turns = Store.GetTurns(job.TrackId, window.SessionStartTicks,
                window.SessionStartTicks + window.SampleCount * TimeSpan.TicksPerSecond / 16000L);
            IReadOnlyList<SegmentDraft> rows;
            if (result.Segments.All(segment => segment.Timing == TimingGranularity.Word && !segment.Words.IsDefaultOrEmpty))
                rows = TranscriptMerger.MergeWords(window, result.Segments.SelectMany(segment => segment.Words)
                    .Select(word => new RelativeRecognizedWord(word.Text, word.StartMilliseconds, word.EndMilliseconds)), turns, provenance);
            else
            {
                if (window.CoreStartSample != 0 || window.SampleCount != window.CoreSampleCount)
                    throw new TranscriptionProviderException(new(ProviderErrorCode.InvalidResponse,
                        "This response omitted usable word timing for overlapping context. Raw source audio is retained; choose a coarse-timed provider explicitly."));
                rows = TranscriptMerger.MergeSegments(window, result.Segments.Select(segment =>
                    new RelativeRecognizedSegment(segment.Text, segment.StartMilliseconds ?? 0,
                        segment.EndMilliseconds ?? (window.SampleCount * 1000L / 16000), segment.Timing.ToString())), turns, provenance);
            }
            Store.CompleteJob(job, rows, evidence, modelIdentity);
            // Speaker analysis runs in its own lane; re-apply any turns it stored meanwhile to the new rows.
            RefreshSpeakerAssignments(job.SessionId, job.TrackId, chunk.StartTicks,
                chunk.StartTicks + chunk.SampleCount * TimeSpan.TicksPerSecond / 16000L);
            TranscriptChanged?.Invoke(job.SessionId);
            if (result.Status != TranscriptionStatus.Succeeded || !result.Diagnostics.IsDefaultOrEmpty)
                Notify($"{provider.Descriptor.Name}: {result.Status}. {string.Join("; ", result.Diagnostics.IsDefault ? [] : result.Diagnostics)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Store.FailJob(job, "Interrupted; the durable source remains available.",
                shutdown.IsCancellationRequested || Volatile.Read(ref leaseLost) != 0 ? "Pending" : "Paused");
        }
        catch (TranscriptionProviderException error)
        {
            var retry = error.Error.IsTransient && job.Attempts < 3;
            var blocked = error.Error.Code is ProviderErrorCode.Authentication or ProviderErrorCode.PermissionDenied or
                ProviderErrorCode.QuotaExceeded or ProviderErrorCode.ConsentRequired or ProviderErrorCode.ModelUnavailable;
            Store.FailJob(job, error.Error.SafeMessage, retry ? "RetryWaiting" : blocked ? "Blocked" : "Failed",
                retry ? error.Error.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, job.Attempts) * 3 + Random.Shared.NextDouble())) : null);
            if (job.Cloud && error.Error.Code is ProviderErrorCode.Authentication or ProviderErrorCode.PermissionDenied or ProviderErrorCode.QuotaExceeded)
            {
                UpdateSettings(current => current with { CloudBlockReason = error.Error.SafeMessage });
                Store.BlockCloudJobs(error.Error.SafeMessage);
                Notify("NVIDIA work is suspended after an authentication, permission, or quota error. Resolve it, then explicitly Resume; no fallback provider was selected.", true);
            }
            Notify($"Transcription { (retry ? "will retry" : "needs attention") }: {error.Error.SafeMessage}", true);
        }
        catch (Exception error) when (IsOperational(error))
        {
            Store.FailJob(job, error.Message);
            Notify("Background job failed; retained audio is available: " + error.Message, true);
        }
        finally
        {
            await leaseLifetime.CancelAsync();
            await lease;
            if (workPath is not null && File.Exists(workPath)) File.Delete(workPath);
        }
    }

    private static TranscriptionResult NormalizeResult(TranscriptionResult result)
    {
        if (result.Segments.IsDefault)
            throw new TranscriptionProviderException(new(ProviderErrorCode.InvalidResponse, "The provider omitted its required segment collection."));
        return result with
        {
            Diagnostics = result.Diagnostics.IsDefault ? [] : result.Diagnostics,
            Segments = result.Segments.Select(segment => segment with
            {
                Words = segment.Words.IsDefault ? [] : segment.Words
            }).ToImmutableArray()
        };
    }

    /// <summary>
    /// When local Whisper is unsure about a chunk, the same audio is re-recognized by Parakeet on this PC (the opted-in GPU
    /// container, else the CPU model), or else by hosted Parakeet if the session explicitly allows NVIDIA uploads.
    /// Any Parakeet failure keeps the local text.
    /// </summary>
    private async Task<(ITranscriptionProvider Provider, TranscriptionResult Result, double Score, double Threshold)?> RunConfidenceFallbackAsync(
        StoredJob job, TranscriptionRequest request, TranscriptionResult local, CancellationToken cancellationToken)
    {
        if (job.ProviderId != "local-whisper" || local.Status != TranscriptionStatus.Succeeded) return null;
        var threshold = settings.FallbackBelowConfidence ?? DefaultFallbackBelowConfidence;
        if (ChunkConfidence(local.Segments) is not { } score || score >= threshold) return null;
        ITranscriptionProvider target;
        if (localGpu is { } gpu && LocalRivaProvider.SupportsLanguage(job.Language)) target = gpu;
        else if (providerOverride is null && ParakeetModelReady && SherpaParakeetProvider.SupportsLanguage(job.Language))
            target = CreateProvider(SherpaParakeetProvider.ProviderId);
        else if (!HasNvidiaKey || fallbackSuspended is not null || settings.CloudBlockReason is not null ||
            !IsFallbackLanguage(job.Language) || !Store.GetSession(job.SessionId).CloudConsent) return null;
        else target = CreateProvider(FallbackProviderId);
        try { return (target, NormalizeResult(await target.TranscribeAsync(request, cancellationToken)), score, threshold); }
        catch (TranscriptionProviderException error)
        {
            if (!target.Descriptor.IsCloud)
                Notify("Local Parakeet failed for one chunk; local Whisper text is kept: " + error.Error.SafeMessage, true);
            else if (error.Error.Code is ProviderErrorCode.Authentication or ProviderErrorCode.PermissionDenied or ProviderErrorCode.QuotaExceeded)
            {
                fallbackSuspended = error.Error.SafeMessage;
                Notify("Hosted Parakeet fallback is off until the NVIDIA key is set again (" + error.Error.SafeMessage + "). Local Whisper text is kept.", true);
            }
            else Notify("Hosted Parakeet fallback failed for one chunk; local Whisper text is kept: " + error.Error.SafeMessage, true);
            return null;
        }
    }

    internal static double? ChunkConfidence(IReadOnlyList<TranscriptionSegment> segments)
    {
        // The least confident segment decides: one garbled phrase is enough to re-check the whole chunk.
        var scores = segments.Where(segment => !string.IsNullOrWhiteSpace(segment.Text) && segment.Confidence is not null)
            .Select(segment => segment.Confidence!.Value).ToArray();
        return scores.Length == 0 ? null : scores.Min();
    }

    private static bool IsFallbackLanguage(string language)
    {
        try { NvidiaModelCatalog.Get(FallbackProviderId).GetLocale(language); return true; }
        catch (ProviderException) { return false; }
    }

    private Guid? EchoReferenceTrack(StoredJob job)
    {
        var track = Store.GetTracks(job.SessionId).FirstOrDefault(item => item.Id == job.TrackId);
        if (track is not { Kind: "Microphone", MetadataJson: { } json }) return null;
        try { return JsonSerializer.Deserialize<MicrophoneOptions>(json)?.EchoReferenceTrackId; }
        catch (JsonException) { return null; }
    }

    private bool EchoReferenceReady(StoredJob job, RecognitionWindow window, IReadOnlyList<StoredAudioChunk> reference, Guid referenceTrackId)
    {
        // AEC needs speaker audio only up to (window end - acoustic delay), so a short missing tail is harmless.
        var needed = checked(window.SessionStartTicks + window.SampleCount * TimeSpan.TicksPerSecond / 16000L - 100 * TimeSpan.TicksPerMillisecond);
        if (reference.Any(item => item.StartTicks + item.SampleCount * TimeSpan.TicksPerSecond / 16000L >= needed)) return true;
        if (!captureFeeds.ContainsKey(referenceTrackId) && !mediaTasks.ContainsKey(job.SessionId)) return true;
        // Loopback produces no packets while nothing plays, so speaker audio still missing long after is silence.
        return RecordingSessionId == job.SessionId && recordingClock.Elapsed.Ticks > needed + TimeSpan.FromSeconds(90).Ticks;
    }

    private async Task<bool> ReduceEchoAsync(RecognitionWindow window, IReadOnlyList<StoredAudioChunk> microphone,
        IReadOnlyList<StoredAudioChunk> reference, CancellationToken cancellationToken)
    {
        try { return await EchoReduction.ApplyAsync(window, microphone, reference, cancellationToken); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Notify("Echo reduction is unavailable; the microphone is transcribed without it: " + error.Message, true);
            return false;
        }
    }

    private async Task ProcessDiarizationAsync(StoredJob job, StoredAudioChunk chunk, CancellationToken cancellationToken)
    {
        // The registry is read before inference and replaced after it; user merges/enrollment must not interleave.
        await registryGate.WaitAsync(cancellationToken);
        try { await ProcessDiarizationCoreAsync(job, chunk, cancellationToken); }
        finally { registryGate.Release(); }
    }

    private SpeakerRegistrySnapshot LoadRegistry(Guid sessionId, out string? json)
    {
        json = Store.GetSpeakerRegistry(sessionId);
        var registry = json is null ? new SpeakerRegistrySnapshot(sessionId, 0, []) : CoreRegistrySerializer.Deserialize(json);
        var names = Store.GetSpeakers(sessionId).ToDictionary(speaker => speaker.Id, speaker => speaker.Name);
        return registry with { Speakers = registry.Speakers.Select(entry =>
            names.TryGetValue(entry.Identity.Id.ToString("D"), out var name)
                ? entry with { Identity = entry.Identity with { DisplayName = name } } : entry).ToImmutableArray() };
    }

    private async Task ProcessDiarizationCoreAsync(StoredJob job, StoredAudioChunk chunk, CancellationToken cancellationToken)
    {
        var window = DiarizationWindow(job, chunk);
        var first = window[0];
        var source = JsonSerializer.Deserialize<NormalizedChunk>(first.MetadataJson)
            ?? throw new InvalidDataException("Normalized source timing metadata is missing.");
        var registry = LoadRegistry(job.SessionId, out var previous);
        var sampleCount = window.Sum(item => item.SampleCount);
        var startTicks = first.StartTicks;
        var endTicks = Math.Max(chunk.StartTicks + chunk.SampleCount * TimeSpan.TicksPerSecond / 16000L,
            startTicks + sampleCount * TimeSpan.TicksPerSecond / 16000L);
        var silent = true;
        foreach (var item in window) silent &= await IsSilentChunkAsync(item, cancellationToken);
        if (silent)
        {
            Store.CompleteDiarizationJob(job, previous ?? CoreRegistrySerializer.Serialize(registry), [], [], startTicks, endTicks);
            return;
        }
        var audioPath = chunk.Path;
        string? workPath = null;
        try
        {
            if (window.Count > 1)
            {
                workPath = Path.Combine(Store.GetSession(job.SessionId).Directory, "work", job.Id.ToString("N") + ".speakers.pcm16");
                Directory.CreateDirectory(Path.GetDirectoryName(workPath)!);
                await using (var output = new FileStream(workPath, FileMode.Create, FileAccess.Write, FileShare.None, 65_536, true))
                    foreach (var item in window)
                    {
                        await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, true);
                        if (input.Length != item.SampleCount * 2L)
                            throw new InvalidDataException("A normalized chunk does not match its committed PCM16 sample count.");
                        await input.CopyToAsync(output, cancellationToken);
                    }
                audioPath = workPath;
            }
            await using var service = CreateDiarizer(job.SessionId);
            var result = await service.DiarizeAsync(new(job.SessionId, job.TrackId, audioPath, sampleCount, startTicks,
                NormalizedStartSample: first.StartSample, SourceFrameOffset: source.SourceFrameOffset, SourceSampleRate: source.SourceFormat.SampleRate),
                registry, cancellationToken);
            Store.SaveRawAttempt(job, JsonSerializer.Serialize(new
            {
                Version = 1, Source = source, WindowChunks = window.Select(item => item.Id), result.Turns,
                Diagnostics = result.Diagnostics.IsDefault ? [] : result.Diagnostics,
                SegmentationSha256 = DiarizationModels.SegmentationModelSha256,
                DiarizationModels.EmbeddingSha256
            }), "local:pyannote-segmentation-3.0+wespeaker-resnet34-lm");
            var turns = result.Turns.Select(turn => new StoredTurn(turn.TrackId, turn.StartTicks, turn.EndTicks,
                turn.SpeakerId?.ToString("D"), turn.Quality.HasFlag(SpeakerQualityFlags.Overlap),
                turn.SpeakerId is null || (turn.Quality & (SpeakerQualityFlags.Ambiguous | SpeakerQualityFlags.InsufficientEvidence)) != 0)).ToArray();
            var speakers = result.Registry.Speakers.Where(entry => entry.Identity.MergedIntoId is null)
                .Select(entry => new StoredSpeaker(entry.Identity.Id.ToString("D"),
                job.SessionId, entry.Identity.DisplayName, entry.Identity.ExternalParticipantId, "Local segmentation and speaker embeddings")).ToArray();
            if (!Store.CompleteDiarizationJob(job, CoreRegistrySerializer.Serialize(result.Registry), speakers, turns,
                    startTicks, endTicks))
            {
                Notify("Speaker result was superseded by a pause, cancellation, or expired lease; the source remains retained.");
                return;
            }
            RefreshSpeakerAssignments(job.SessionId, job.TrackId, startTicks, endTicks);
            TranscriptChanged?.Invoke(job.SessionId);
            // Routine markers (algorithm version, short-tail padding, silence) are kept in the raw attempt, not announced.
            var notable = (result.Diagnostics.IsDefault ? [] : result.Diagnostics).Where(item => item != SherpaDiarizationService.AlgorithmVersion &&
                !item.StartsWith("ShortInputSilencePadded", StringComparison.Ordinal) && item != "NoSpeechDetected").ToArray();
            if (notable.Length > 0) Notify("Speaker analysis: " + string.Join("; ", notable));
        }
        finally
        {
            if (workPath is not null && File.Exists(workPath)) File.Delete(workPath);
        }
    }

    // The job's chunk plus the contiguous preceding chunks that were queued without a speaker job of their own.
    private List<StoredAudioChunk> DiarizationWindow(StoredJob job, StoredAudioChunk chunk)
    {
        var chunks = Store.GetChunks(job.TrackId);
        var window = new List<StoredAudioChunk> { chunk };
        long total = chunk.SampleCount;
        var index = -1;
        for (var i = 0; i < chunks.Count; i++) if (chunks[i].Id == chunk.Id) { index = i; break; }
        for (var i = index - 1; i >= 0; i--)
        {
            var candidate = chunks[i];
            if (total + candidate.SampleCount > Pcm16Audio.MaximumSamples ||
                !RecognitionWindowBuilder.Adjacent(candidate, window[0]) || Store.HasJob(candidate.Id, DiarizationProvider))
                break;
            window.Insert(0, candidate);
            total += candidate.SampleCount;
        }
        return window;
    }

    private static async Task<bool> IsSilentChunkAsync(StoredAudioChunk chunk, CancellationToken cancellationToken)
    {
        // Invalid audio is left to the normal path, which reports it on the affected job.
        try { return Pcm16Audio.IsSilent(await Pcm16Audio.ReadAsync(chunk.Path, chunk.SampleCount, cancellationToken)); }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentOutOfRangeException) { return false; }
    }

    private void RefreshSpeakerAssignments(Guid sessionId, Guid trackId, long startTicks, long endTicks)
    {
        TranscriptCursor? cursor = null;
        while (true)
        {
            var page = Store.GetTranscriptPage(sessionId, after: cursor, seekTicks: startTicks);
            var assignments = new List<(string SegmentId, string? SpeakerId, bool Uncertain)>();
            foreach (var row in page)
            {
                if (row.StartTicks >= endTicks) break;
                if (row.TrackId != trackId || row.Provenance.StartsWith("WebVTT ", StringComparison.Ordinal)) continue;
                var match = TranscriptMerger.MatchSpeaker(row.StartTicks, row.EndTicks,
                    Store.GetTurns(trackId, row.StartTicks, Math.Max(row.EndTicks, row.StartTicks + 1)));
                assignments.Add((row.Id, match.Speaker, match.Uncertain));
            }
            Store.ApplyAutomaticSpeakerAssignments(assignments);
            if (page.Count < 200 || page[^1].StartTicks >= endTicks) return;
            cursor = new(page[^1].StartTicks, page[^1].Id);
        }
    }

    private async Task RenewLeaseAsync(StoredJob job, Action leaseLost, CancellationToken cancellationToken)
    {
        try
        {
            var delay = TimeSpan.FromSeconds(30);
            while (true)
            {
                await Task.Delay(delay, cancellationToken);
                try
                {
                    if (!Store.TryRenewLease(job))
                    {
                        Notify("The active job lost its durable lease; inference is being canceled for safe replay.", true);
                        leaseLost();
                        return;
                    }
                    delay = TimeSpan.FromSeconds(30);
                }
                catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
                {
                    Notify("Lease renewal is temporarily unavailable; retained audio is safe: " + error.Message, true);
                    delay = TimeSpan.FromSeconds(5);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private ITranscriptionProvider CreateProvider(string id)
    {
        var cacheKey = id == "local-whisper" ? id + ":" + settings.WhisperModelPath : id;
        if (providerCache.TryGetValue(cacheKey, out var existing)) return existing;
        if (id == "local-whisper")
        {
            foreach (var old in providerCache.Keys.Where(key => key.StartsWith("local-whisper:", StringComparison.Ordinal)).ToArray())
            {
                providerCache[old].DisposeAsync().AsTask().GetAwaiter().GetResult();
                providerCache.Remove(old);
            }
        }
        var provider = MakeProvider(id);
        providerCache.Add(cacheKey, provider);
        return provider;
    }

    private ITranscriptionProvider MakeProvider(string id)
    {
        if (providerOverride is not null) return providerOverride(id);
        if (id == "local-whisper")
        {
            if (string.IsNullOrWhiteSpace(settings.WhisperModelPath))
                throw new TranscriptionProviderException(new(ProviderErrorCode.ModelUnavailable,
                    "No local Whisper model is installed yet. Install the recommended model from Privacy / models; this audio is transcribed automatically afterward. No cloud fallback was used."));
            return new LocalWhisperProvider(settings.WhisperModelPath);
        }
        if (id == SherpaParakeetProvider.ProviderId) return new SherpaParakeetProvider(ParakeetModelDirectory);
        return new NvidiaRivaProvider(NvidiaModelCatalog.Get(id), credentials, new ConsentReader(Store));
    }

    private ProviderOption RequireProvider(string id) => Providers.SingleOrDefault(provider => provider.Id == id)
        ?? throw new ArgumentException("Choose an available provider from the local catalog.");
    private static Core.NativeChunk DeserializeNative(string json) => JsonSerializer.Deserialize<Core.NativeChunk>(json)
        ?? throw new InvalidDataException("The native chunk manifest is invalid.");
    private static MediaCheckpoint ReadImport(StoredTrack track) => JsonSerializer.Deserialize<MediaCheckpoint>(track.MetadataJson ?? "")
        ?? throw new InvalidDataException("The managed import checkpoint is invalid.");
    private void Notify(string message, bool error = false) => Notification?.Invoke(new(message, error));
    private void UpdateSettings(Func<LocalSettings, LocalSettings> update)
    {
        lock (settingsGate)
        {
            var next = update(settings);
            var temporary = settingsPath + ".partial";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, next);
                    stream.Flush(true);
                }
                File.Move(temporary, settingsPath, true);
                settings = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static bool IsOperational(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or JsonException or TimeoutException or
        Microsoft.Data.Sqlite.SqliteException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException;

    private static string? FindCheckout()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AudioTranscriber.slnx"))) return directory.FullName;
        return null;
    }

    private IDiarizationService CreateDiarizer(Guid sessionId)
    {
        if (diarizationOverride is not null) return diarizationOverride();
        var (worker, host) = FindWorker();
        return new WorkerDiarizationService(worker, Path.Combine(Store.GetSession(sessionId).Directory, "workers"),
            DiarizationModelPaths.InDirectory(modelDirectory), dotnetHostPath: host);
    }

    private static (string Worker, string? Host) FindWorker()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "AudioTranscriber.Worker.exe");
        // Development builds copy the referenced worker's apphost without its assembly; only a complete worker is usable.
        if (File.Exists(local) && File.Exists(Path.ChangeExtension(local, ".dll"))) return (local, null);
        var root = FindCheckout();
        if (root is not null)
        {
            var preferred = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
            foreach (var configuration in new[] { preferred, preferred == "Release" ? "Debug" : "Release" })
            {
                var candidate = Path.Combine(root, "src", "AudioTranscriber.Worker", "bin", configuration,
                    "net10.0-windows", "win-x64", "AudioTranscriber.Worker.dll");
                var host = Path.Combine(root, ".tools", "dotnet", "dotnet.exe");
                if (File.Exists(candidate) && File.Exists(host)) return (candidate, host);
            }
        }
        throw new FileNotFoundException("The local diarization worker is missing. Build or publish the complete application.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { await StopRecordingAsync(CancellationToken.None); }
        finally
        {
            stopping = true;
            try
            {
                Notify("Closing: finishing the current recognition response before checkpointing remaining work.");
                var active = new[] { speechLane.Task, speakerLane.Task }.OfType<Task>().ToArray();
                if (active.Length > 0)
                    await Task.WhenAny(Task.WhenAll(active), Task.Delay(TimeSpan.FromSeconds(65)));
                await shutdown.CancelAsync();
                wake.Release();
                await Task.WhenAll(scheduler, speakerScheduler);
                await Task.WhenAll(mediaTasks.Values);
                await Task.WhenAll(backgroundTasks.Values);
                if (modelSetup is { } setup) await Task.WhenAny(setup, Task.Delay(TimeSpan.FromSeconds(10)));
                if (localGpuStart is { } gpuStart) await Task.WhenAny(gpuStart, Task.Delay(TimeSpan.FromSeconds(10)));
                // Free the GPU memory; the stopped container keeps its downloaded, optimized model for the next start.
                if (nimHost is { } host) await Task.WhenAny(host.StopAsync(), Task.Delay(TimeSpan.FromSeconds(20)));
            }
            finally
            {
                await shutdown.CancelAsync();
                try
                {
                    var cleanup = new List<Task> { playback.DisposeAsync().AsTask(), capture.DisposeAsync().AsTask() };
                    if (timelinePlayer is not null) cleanup.Add(timelinePlayer.DisposeAsync().AsTask());
                    cleanup.AddRange(providerCache.Values.Select(provider => provider.DisposeAsync().AsTask()));
                    if (localGpu is { } gpu) cleanup.Add(gpu.DisposeAsync().AsTask());
                    await Task.WhenAll(cleanup);
                }
                finally
                {
                    credentials.Dispose();
                    Store.CloseConnections();
                    libraryLock.Dispose();
                    shutdown.Dispose();
                }
            }
        }
    }

    private sealed record LocalSettings(string? WhisperModelPath, string? CloudBlockReason = null, double? FallbackBelowConfidence = null,
        bool LocalGpuParakeet = false);
    private sealed record MicrophoneOptions(Guid? EchoReferenceTrackId);
    private sealed record MediaCheckpoint(string SourcePath, int StreamIndex, ImportedMedia? Imported);
    private sealed class CaptureFeed
    {
        public Channel<byte> Signals { get; } = Channel.CreateBounded<byte>(1);
        public Task Task { get; set; } = Task.CompletedTask;
    }
    private sealed class JobLane
    {
        public volatile Task? Task;
        public volatile CancellationTokenSource? Cancellation;
        public Guid? Session;
        public volatile bool Cloud;
    }
    private sealed class WakeSignal
    {
        public SemaphoreSlim Speech { get; } = new(0);
        public SemaphoreSlim Speaker { get; } = new(0);
        public void Release() { Speech.Release(); Speaker.Release(); }
    }
    private sealed class ConsentReader(LibraryStore store) : ICloudConsentStore
    {
        public Task<CloudConsent?> GetAsync(Guid sessionId, string providerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = store.GetSession(sessionId);
            // A local-Whisper session's upload consent covers only the hosted low-confidence fallback route.
            var granted = session.CloudConsent && (session.ProviderId == providerId ||
                session.ProviderId == "local-whisper" && providerId == FallbackProviderId);
            return Task.FromResult<CloudConsent?>(new(sessionId, providerId,
                store.GetTracks(sessionId).Select(track => track.Id).ToImmutableArray(),
                granted ? ConsentState.Granted : ConsentState.NotGranted,
                DateTimeOffset.UtcNow, "nvidia-upload-disclosure-2026-09-11"));
        }
    }
}
