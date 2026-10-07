using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Threading;
using AudioTranscriber.App.Templates;
using AudioTranscriber.Application;
using AudioTranscriber.Storage;

namespace AudioTranscriber.App;

/// <summary>
/// Output templates: user-written prompts that an LLM (OpenRouter, NVIDIA Build, Ollama, any OpenAI-compatible server)
/// keeps re-running against the live transcript, optionally reading the user's reference files, with the result shown
/// in the app and optionally mirrored to an unlocked text file.
/// </summary>
public sealed class TemplatesViewModel : ObservableObject
{
    private const int MaxReferenceChars = 60000;
    private readonly IAppController controller;
    private readonly DesktopDialogs dialogs;
    private readonly Dispatcher dispatcher;
    private readonly Func<Guid?> targetSession;
    private readonly Action<string, bool> log;
    private readonly DispatcherTimer timer;
    private readonly Dictionary<Guid, CancellationTokenSource> running = new();
    private LlmConnection? selectedConnection;
    private OutputTemplate? selectedTemplate;
    private LlmPreset selectedPreset = LlmPreset.All[0];
    private string contextFolder = "", pinnedFiles = "", connectionStatus = "", targetDescription = "No session selected.";
    private string captureTarget = "", captureCaption = "No screenshot taken yet.";
    private int captureMaxWidth = ScreenCapture.DefaultMaxWidth;
    private Avalonia.Media.Imaging.Bitmap? capturePreview;
    private bool dirty, ticking, closing, loadingModels, capturing;

    public TemplatesViewModel(IAppController controller, DesktopDialogs dialogs, Dispatcher dispatcher, Func<Guid?> targetSession,
        Action<string, bool> log)
    {
        this.controller = controller;
        this.dialogs = dialogs;
        this.dispatcher = dispatcher;
        this.targetSession = targetSession;
        this.log = log;
        Load();
        AddConnectionCommand = new RelayCommand(AddConnection);
        RemoveConnectionCommand = new AsyncCommand(RemoveConnectionAsync, () => SelectedConnection is not null);
        LoadModelsCommand = new AsyncCommand(LoadModelsAsync, () => SelectedConnection is not null && !loadingModels);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () => SelectedConnection is not null && !loadingModels);
        CheckImageSupportCommand = new AsyncCommand(CheckImageSupportAsync, () => SelectedConnection is not null && !loadingModels);
        AddTemplateCommand = new RelayCommand(() => AddTemplate(new OutputTemplate { Name = "New template", Prompt = "Describe what to produce from the transcript…" }));
        DuplicateTemplateCommand = new RelayCommand(DuplicateTemplate, () => SelectedTemplate is not null);
        DeleteTemplateCommand = new AsyncCommand(DeleteTemplateAsync, () => SelectedTemplate is not null);
        AddStartersCommand = new RelayCommand(() => { foreach (var t in OutputTemplate.Starters()) AddTemplate(t); });
        AddTableStartersCommand = new RelayCommand(AddTableStarters);
        RunTemplateCommand = new RelayCommand(() => { if (SelectedTemplate is { } t) _ = RunAsync(t, manual: true); },
            () => SelectedTemplate is { IsRunning: false });
        StopTemplateCommand = new RelayCommand(() => { if (SelectedTemplate is { } t && running.TryGetValue(t.Id, out var c)) c.Cancel(); },
            () => SelectedTemplate is { IsRunning: true });
        BrowseOutputCommand = new AsyncCommand(BrowseOutputAsync, () => SelectedTemplate is not null);
        BrowseFolderCommand = new AsyncCommand(async () => { if (await dialogs.ChooseFolderAsync(ContextFolder) is { } folder) ContextFolder = folder; });
        AddPinnedFilesCommand = new AsyncCommand(AddPinnedFilesAsync);
        RefreshCaptureSourcesCommand = new AsyncCommand(RefreshCaptureSourcesAsync);
        TestCaptureCommand = new AsyncCommand(async () =>
        {
            try { await CaptureAsync(); }
            catch (CaptureException error) { CaptureCaption = "Capture failed: " + error.Message; }
        }, () => !capturing);
        CopyOutputCommand = new AsyncCommand(async () =>
        {
            await DesktopDialogs.SetClipboardTextAsync(SelectedTemplate?.Output ?? "");
        }, () => !string.IsNullOrEmpty(SelectedTemplate?.Output));
        timer = NewTimer(TimeSpan.FromSeconds(4), (_, _) => _ = TickAsync());
        timer.Stop();
        Templates.CollectionChanged += (_, _) => RebuildInputOptions();
        RebuildInputOptions();
    }

    private static DispatcherTimer NewTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    public ObservableCollection<LlmConnection> Connections { get; } = [];
    public ObservableCollection<OutputTemplate> Templates { get; } = [];
    /// <summary>Checklist of the other templates the selected template can use as inputs.</summary>
    public ObservableCollection<TemplateInputOption> TemplateInputs { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<string> CaptureSources { get; } = [];
    public IReadOnlyList<LlmPreset> Presets => LlmPreset.All;
    public ICommand RefreshCaptureSourcesCommand { get; }
    public ICommand TestCaptureCommand { get; }

    public ICommand AddConnectionCommand { get; }
    public ICommand RemoveConnectionCommand { get; }
    public ICommand LoadModelsCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand CheckImageSupportCommand { get; }
    public ICommand AddTemplateCommand { get; }
    public ICommand DuplicateTemplateCommand { get; }
    public ICommand DeleteTemplateCommand { get; }
    public ICommand AddStartersCommand { get; }
    public ICommand AddTableStartersCommand { get; }
    public ICommand RunTemplateCommand { get; }
    public ICommand StopTemplateCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand AddPinnedFilesCommand { get; }
    public ICommand CopyOutputCommand { get; }

    public LlmPreset SelectedPreset { get => selectedPreset; set { if (Set(ref selectedPreset, value ?? LlmPreset.All[0])) Changed(nameof(PresetHelp)); } }
    public string PresetHelp => SelectedPreset.Help;

    public LlmConnection? SelectedConnection
    {
        get => selectedConnection;
        set
        {
            if (!Set(ref selectedConnection, value)) return;
            Models.Clear();
            ConnectionStatus = "";
            Changed(nameof(HasConnection));
        }
    }
    /// <summary>Picking from the loaded list fills in the connection's model.</summary>
    public string? PickedModel
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && SelectedConnection is { } connection) connection.Model = value; Changed(); }
    }
    public bool HasConnection => SelectedConnection is not null;
    public string ConnectionStatus { get => connectionStatus; private set => Set(ref connectionStatus, value); }

    public OutputTemplate? SelectedTemplate
    {
        get => selectedTemplate;
        set { if (Set(ref selectedTemplate, value)) { Changed(nameof(HasTemplate)); RebuildInputOptions(); } }
    }
    public bool HasTemplate => SelectedTemplate is not null;

    private void RebuildInputOptions()
    {
        TemplateInputs.Clear();
        if (SelectedTemplate is not { } selected) return;
        foreach (var template in Templates.Where(t => t != selected))
            TemplateInputs.Add(new TemplateInputOption(template, selected.InputTemplateIds.Contains(template.Id), true, "", ToggleInput));
        RefreshInputOptions();
    }

    // A template that already (transitively) uses the selected one can't become its input: that would loop forever.
    private void RefreshInputOptions()
    {
        if (SelectedTemplate is not { } selected) return;
        foreach (var option in TemplateInputs)
        {
            var loops = TemplateGraph.DependsOn(option.Template, selected, Templates);
            option.IsEnabled = option.IsSelected || !loops;
            option.Hint = loops
                ? $"\"{option.Template.Name}\" already uses this template's output, so it can't also be an input (that would loop)."
                : "Feed this template's latest output in. With automatic updates on, this template re-runs when it changes.";
        }
    }

    private void ToggleInput(TemplateInputOption option)
    {
        if (SelectedTemplate is not { } selected) return;
        if (option.IsSelected && TemplateGraph.DependsOn(option.Template, selected, Templates))
        {
            selected.Status = $"\"{option.Template.Name}\" already uses this template, so it can't be an input.";
            option.IsSelected = false;
            return;
        }
        selected.InputTemplateIds = TemplateInputs.Where(o => o.IsSelected).Select(o => o.Template.Id).ToList();
        RefreshInputOptions();
    }

    public string ContextFolder { get => contextFolder; set { if (Set(ref contextFolder, value?.Trim() ?? "")) dirty = true; } }
    public string PinnedFiles { get => pinnedFiles; set { if (Set(ref pinnedFiles, value ?? "")) dirty = true; } }
    public string TargetDescription { get => targetDescription; private set => Set(ref targetDescription, value); }

    public string CaptureTarget { get => captureTarget; set { if (Set(ref captureTarget, value?.Trim() ?? "")) dirty = true; } }
    public int CaptureMaxWidth { get => captureMaxWidth; set { if (Set(ref captureMaxWidth, Math.Clamp(value, 320, 7680))) dirty = true; } }
    public Avalonia.Media.Imaging.Bitmap? CapturePreview { get => capturePreview; private set => Set(ref capturePreview, value); }
    public string CaptureCaption { get => captureCaption; private set => Set(ref captureCaption, value); }

    public void Start() { timer.Start(); _ = RefreshCaptureSourcesAsync(); _ = TickAsync(); }

    private async Task RefreshCaptureSourcesAsync()
    {
        var sources = await Task.Run(ScreenCapture.ListSources);
        var text = CaptureTarget;
        CaptureSources.Clear();
        foreach (var source in sources) CaptureSources.Add(source);
        CaptureTarget = text;
    }

    /// <summary>Takes a screenshot of the shared capture target off the UI thread and shows it as the preview.</summary>
    private async Task<CaptureResult> CaptureAsync()
    {
        capturing = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var target = CaptureTarget;
            var width = CaptureMaxWidth;
            var shot = await Task.Run(() =>
            {
                try { return ScreenCapture.Capture(target, width); }
                catch (Exception error) when (error is not CaptureException) { throw new CaptureException(error.Message); }
            });
            CapturePreview = shot.Preview;
            CaptureCaption = shot.Caption;
            return shot;
        }
        finally { capturing = false; CommandManager.InvalidateRequerySuggested(); }
    }

    public void Shutdown()
    {
        closing = true;
        timer.Stop();
        foreach (var cancellation in running.Values) cancellation.Cancel();
        Save();
    }

    public void SetKey(string key)
    {
        if (SelectedConnection is not { } connection) return;
        connection.SetKey(key);
        Save();
        ConnectionStatus = string.IsNullOrWhiteSpace(key) ? "API key removed." : $"API key saved, {AudioTranscriber.Providers.UserSecretProtection.Description}.";
    }

    // ---------- persistence ----------

    private string SettingsPath => Path.Combine(controller.Store.RootDirectory, "templates.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void Load()
    {
        TemplateSettings? saved = null;
        try { if (File.Exists(SettingsPath)) saved = JsonSerializer.Deserialize<TemplateSettings>(File.ReadAllText(SettingsPath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        if (saved is null)
        {
            var ollama = LlmPreset.All.First(p => p.Name == "Ollama");
            saved = new TemplateSettings
            {
                Connections = [new LlmConnection { Name = ollama.Name, Kind = ollama.Name, BaseUrl = ollama.BaseUrl, Model = ollama.Model }],
                Templates = OutputTemplate.Starters().ToList()
            };
            dirty = true;
            freshDefaults = true;
        }
        contextFolder = saved.ContextFolder ?? "";
        pinnedFiles = saved.PinnedFiles ?? "";
        captureTarget = saved.CaptureTarget ?? "";
        captureMaxWidth = saved.CaptureMaxWidth > 0 ? Math.Clamp(saved.CaptureMaxWidth, 320, 7680) : ScreenCapture.DefaultMaxWidth;
        foreach (var connection in saved.Connections) Track(connection, Connections);
        foreach (var template in saved.Templates) Track(template, Templates);
        AssignDefaultConnection();
        selectedConnection = Connections.FirstOrDefault();
        selectedTemplate = Templates.FirstOrDefault();
    }

    private void Track<T>(T item, ObservableCollection<T> list) where T : INotifyPropertyChanged
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(OutputTemplate.Status) or nameof(OutputTemplate.IsRunning) or nameof(OutputTemplate.Summary))) dirty = true;
        };
        list.Add(item);
    }

    private void Save()
    {
        dirty = false;
        try
        {
            var settings = new TemplateSettings
            {
                Connections = Connections.ToList(),
                Templates = Templates.ToList(),
                ContextFolder = ContextFolder,
                PinnedFiles = PinnedFiles,
                CaptureTarget = CaptureTarget,
                CaptureMaxWidth = CaptureMaxWidth
            };
            var path = SettingsPath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { dirty = true; }
    }

    // ---------- connections ----------

    private void AddConnection()
    {
        var preset = SelectedPreset;
        var connection = new LlmConnection { Name = UniqueName(preset.Name), Kind = preset.Name, BaseUrl = preset.BaseUrl, Model = preset.Model };
        Track(connection, Connections);
        AssignDefaultConnection();
        SelectedConnection = connection;
        dirty = true;
        ConnectionStatus = preset.NeedsKey ? "Added. Paste the API key below and choose Save key, then Load models." : "Added. Choose Load models to see what the server offers.";
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; Connections.Any(c => c.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++) candidate = $"{name} {i}";
        return candidate;
    }

    private async Task RemoveConnectionAsync()
    {
        if (SelectedConnection is not { } connection) return;
        if (!await dialogs.ConfirmAsync("Remove connection", $"Remove the LLM connection \"{connection.Name}\" and its saved API key?")) return;
        Connections.Remove(connection);
        AssignDefaultConnection();
        SelectedConnection = Connections.FirstOrDefault();
        Save();
    }

    // Templates without a valid connection use the first one, so the picker never shows blank.
    private void AssignDefaultConnection()
    {
        if (Connections.FirstOrDefault() is not { } first) return;
        foreach (var template in Templates.Where(t => Connections.All(c => c.Id != t.ConnectionId))) template.ConnectionId = first.Id;
    }

    private async Task LoadModelsAsync()
    {
        if (SelectedConnection is not { } connection) return;
        loadingModels = true;
        ConnectionStatus = "Loading models…";
        try
        {
            var models = await LlmClient.ListModelsAsync(connection.BaseUrl, connection.GetKey(), CancellationToken.None);
            Models.Clear();
            foreach (var model in models) Models.Add(model);
            ConnectionStatus = models.Count == 0 ? "The server listed no models; type the model name." : $"{models.Count:N0} models available: pick one from the list, or type its name.";
        }
        catch (Exception error) when (error is LlmException or HttpRequestException or TaskCanceledException)
        {
            ConnectionStatus = "Could not load models: " + Explain(error);
        }
        finally { loadingModels = false; CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task TestConnectionAsync()
    {
        if (SelectedConnection is not { } connection) return;
        loadingModels = true;
        ConnectionStatus = $"Testing {connection.Model}…";
        try
        {
            var reply = await LlmClient.CompleteAsync(connection.BaseUrl, connection.GetKey(), connection.Model,
                "You are a connectivity check.", "Reply with exactly: OK", null, null, CancellationToken.None);
            ConnectionStatus = $"Connected. {connection.Model} replied: {(reply.Length > 80 ? reply[..80] + "…" : reply)}";
            ConnectionStatus += " " + await CheckImagesAsync(connection);
        }
        catch (Exception error) when (error is LlmException or HttpRequestException or TaskCanceledException)
        {
            ConnectionStatus = "Test failed: " + Explain(error);
        }
        finally { loadingModels = false; CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task CheckImageSupportAsync()
    {
        if (SelectedConnection is not { } connection) return;
        loadingModels = true;
        ConnectionStatus = $"Checking whether {connection.Model} reads images…";
        try { ConnectionStatus = await CheckImagesAsync(connection); }
        finally { loadingModels = false; CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task<string> CheckImagesAsync(LlmConnection connection)
    {
        var model = connection.Model;
        var supported = await LlmClient.SupportsImagesAsync(connection.BaseUrl, connection.GetKey(), model, CancellationToken.None);
        connection.SetImageSupport(model, supported);
        dirty = true;
        return connection.ImageSupportStatus + ".";
    }

    private static string Explain(Exception error) => error switch
    {
        TaskCanceledException => "the request timed out.",
        HttpRequestException http => "could not reach the server (" + http.Message + "). Check the base URL and that the server is running.",
        _ => error.Message
    };

    // ---------- templates ----------

    private void AddTemplate(OutputTemplate template)
    {
        Track(template, Templates);
        AssignDefaultConnection();
        SelectedTemplate = template;
        dirty = true;
    }

    private void AddTableStarters()
    {
        var added = OutputTemplate.TableStarters();
        foreach (var t in added) AddTemplate(t);
        var reminders = added[^1];
        SelectedTemplate = reminders;
        if (string.IsNullOrWhiteSpace(CaptureTarget))
            reminders.Status = "Pick your virtual tabletop window (e.g. the Roll20 browser window) in the Table screenshot card so the table templates can read it.";
    }

    private void DuplicateTemplate()
    {
        if (SelectedTemplate is not { } source) return;
        AddTemplate(new OutputTemplate
        {
            Name = source.Name + " (copy)", Prompt = source.Prompt, ConnectionId = source.ConnectionId, IntervalSeconds = source.IntervalSeconds,
            IncludePrevious = source.IncludePrevious, UseReferences = source.UseReferences, MaxTranscriptChars = source.MaxTranscriptChars,
            UseTranscript = source.UseTranscript, InputTemplateIds = [.. source.InputTemplateIds], UseScreenshot = source.UseScreenshot
        });
    }

    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is not { } template) return;
        if (!await dialogs.ConfirmAsync("Delete template", $"Delete the template \"{template.Name}\"? Its output file, if any, is left in place.")) return;
        if (running.TryGetValue(template.Id, out var cancellation)) cancellation.Cancel();
        foreach (var other in Templates.Where(t => t.InputTemplateIds.Contains(template.Id)))
            other.InputTemplateIds = other.InputTemplateIds.Where(id => id != template.Id).ToList();
        Templates.Remove(template);
        SelectedTemplate = Templates.FirstOrDefault();
        Save();
    }

    private async Task BrowseOutputAsync()
    {
        if (SelectedTemplate is not { } template) return;
        if (await dialogs.SaveTemplateOutputAsync(template.OutputPath, template.Name) is { } path) { template.OutputPath = path; template.WriteToFile = true; }
    }

    private async Task AddPinnedFilesAsync()
    {
        var files = await dialogs.ChooseReferenceFilesAsync(ContextFolder);
        if (files.Count == 0) return;
        var existing = PinnedList();
        var added = files.Where(f => !existing.Contains(f, StringComparer.OrdinalIgnoreCase)).ToArray();
        PinnedFiles = string.Join(Environment.NewLine, existing.Concat(added));
    }

    private string[] PinnedList() => PinnedFiles.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.Trim('"')).Where(p => p.Length > 0).ToArray();

    private LlmConnection? ConnectionFor(OutputTemplate template) =>
        Connections.FirstOrDefault(c => c.Id == template.ConnectionId) ?? Connections.FirstOrDefault();

    // ---------- hardware-aware defaults ----------

    private bool freshDefaults;

    private static bool IsOnThisPc(LlmConnection connection) =>
        Uri.TryCreate(connection.BaseUrl.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>True when templates run on an LLM server on this PC, which then needs GPU memory.</summary>
    public bool UsesLocalLlm => Templates.Count == 0 ? Connections.Any(IsOnThisPc)
        : Templates.Any(t => ConnectionFor(t) is { } c && IsOnThisPc(c));

    /// <summary>True once, when the connections were just created with the app's defaults (first start).</summary>
    public bool TakeFreshDefaults()
    {
        var fresh = freshDefaults;
        freshDefaults = false;
        return fresh;
    }

    /// <summary>Points Ollama connections on this PC that use a stock Gemma 4 size (or none) at <paramref name="model"/>.</summary>
    public IReadOnlyList<string> UseRecommendedLlm(string model)
    {
        var changed = new List<string>();
        foreach (var connection in Connections.Where(c => IsOnThisPc(c) && c.Kind == "Ollama" && c.Model != model &&
                     (c.Model.Length == 0 || AudioTranscriber.Providers.HardwareAdvisor.LlmLadder.Any(m => m.Model == c.Model))))
        {
            connection.Model = model;
            changed.Add(connection.Name);
        }
        if (changed.Count > 0) dirty = true;
        return changed;
    }

    // ---------- running ----------

    private async Task TickAsync()
    {
        if (ticking || closing) return;
        ticking = true;
        try
        {
            if (dirty) Save();
            var store = controller.Store;
            Guid? session = null;
            if (targetSession() is not { } id)
                TargetDescription = "No session selected. Templates that use the transcript run on the session being recorded, or the session selected on the left.";
            else
            {
                try
                {
                    var name = store.GetSession(id).Name;
                    session = id;
                    TargetDescription = controller.RecordingSessionId == id ? $"Following the recording: \"{name}\"" : $"Using the selected session: \"{name}\"";
                }
                catch (InvalidOperationException) { }
            }
            var due = TemplateGraph.RunOrder(Templates).Where(t => t.AutoUpdate && !t.IsRunning &&
                DateTime.UtcNow - t.LastRunUtc >= TimeSpan.FromSeconds(t.IntervalSeconds)).ToArray();
            if (due.Length == 0) return;
            string? transcript = null;
            var rows = 0;
            if (session is { } sessionId && due.Any(t => t.UseTranscript))
                (transcript, rows) = await Task.Run(() => (LiveTranscriptFile.Render(store, sessionId, out var count), count));
            if (closing) return;
            // One screenshot per tick, shared by every template that uses it, so they all see the same frame.
            CaptureResult? shot = null;
            string? captureError = null;
            if (due.Any(t => t.UseScreenshot && !t.IsRunning && (!t.UseTranscript || rows > 0)))
            {
                try { shot = await CaptureAsync(); }
                catch (CaptureException error) { captureError = error.Message; }
            }
            // Upstream first: a template started here is already IsRunning, so its downstream waits for the fresh output.
            foreach (var template in due)
            {
                var inputs = TemplateGraph.Inputs(template, Templates);
                if (template.IsRunning || inputs.Any(i => i.IsRunning)) continue;
                if (template.UseTranscript && rows == 0) continue;
                if (template.UseScreenshot && shot is null)
                {
                    template.Status = $"Screenshot failed {DateTime.Now:HH:mm:ss}: {captureError}";
                    continue;
                }
                if (template.LastFingerprint != TemplateGraph.Fingerprint(template, session, transcript, inputs.Select(i => (i.Id, i.Output)), ExtraFingerprint(template, shot)))
                    _ = RunAsync(template, manual: false, session, transcript, shot);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        finally { ticking = false; }
    }

    /// <summary>Extra per-template inputs that should trigger re-runs: the screenshot's thumbnail hash.</summary>
    private static string? ExtraFingerprint(OutputTemplate template, CaptureResult? shot) =>
        template.UseScreenshot && shot is not null ? "screen:" + shot.Fingerprint : null;

    private async Task RunAsync(OutputTemplate template, bool manual, Guid? sessionId = null, string? transcript = null, CaptureResult? shot = null)
    {
        if (template.IsRunning || closing) return;
        if (ConnectionFor(template) is not { } connection) { template.Status = "Add an LLM connection below first."; return; }
        var session = sessionId ?? targetSession();
        if (template.UseTranscript && session is null) { template.Status = "Select or record a session first (or untick Transcript in this template's inputs)."; return; }
        if (template.UseScreenshot && connection.SupportsImages == false)
        {
            template.LastRunUtc = DateTime.UtcNow;
            template.Status = $"{connection.Model} can't read images. Pick a vision model such as gemma4:e4b, or untick the screenshot input.";
            return;
        }
        var cancellation = new CancellationTokenSource();
        running[template.Id] = cancellation;
        template.IsRunning = true;
        template.LastRunUtc = DateTime.UtcNow;
        CommandManager.InvalidateRequerySuggested();
        var started = DateTime.Now;
        try
        {
            if (template.UseTranscript && transcript is null)
            {
                var store = controller.Store;
                var id = session!.Value;
                transcript = await Task.Run(() => LiveTranscriptFile.Render(store, id));
            }
            if (template.UseScreenshot && shot is null)
            {
                template.Status = "Taking a screenshot…";
                shot = await CaptureAsync();
            }
            IReadOnlyList<byte[]>? images = template.UseScreenshot && shot is not null ? [shot.Jpeg] : null;
            var inputs = TemplateGraph.Inputs(template, Templates).Select(t => (t.Id, t.Name, t.Output)).ToArray();
            var fingerprint = TemplateGraph.Fingerprint(template, session, transcript, inputs.Select(i => (i.Id, i.Output)), ExtraFingerprint(template, shot));
            var previous = template.IncludePrevious && (!template.UseTranscript || template.OutputSessionId == session) ? template.Output : "";
            var library = template.UseReferences ? new ReferenceLibrary(ContextFolder, PinnedList()) : null;
            var references = library is null ? "" : await Task.Run(() => library.PinnedText(MaxReferenceChars), cancellation.Token);
            var tools = library is { HasFolder: true } ? library : null;
            var system = BuildSystem(tools is not null, images is not null);
            var user = BuildUser(template, template.UseTranscript ? transcript : null, inputs.Select(i => (i.Name, i.Output)).ToArray(), references, previous);
            var progress = new Progress<string>(message => template.Status = message);
            var key = connection.GetKey();
            var result = await Task.Run(() => LlmClient.CompleteAsync(connection.BaseUrl, key, connection.Model, system, user, tools, progress, cancellation.Token, images));
            if (string.IsNullOrWhiteSpace(result)) throw new LlmException("The model returned an empty answer.");
            template.Output = result;
            template.OutputSessionId = template.UseTranscript ? session : null;
            template.LastFingerprint = fingerprint;
            var seconds = (DateTime.Now - started).TotalSeconds;
            var status = $"Updated {DateTime.Now:HH:mm:ss} in {seconds:0.#} s · {connection.Name} · {connection.Model}";
            if (template.WriteToFile && template.OutputPath.Trim() is { Length: > 0 } path)
            {
                var bytes = await Task.Run(() => LiveTranscriptFile.Write(path, result + Environment.NewLine));
                status += $" · {bytes:N0} bytes → {path}";
            }
            template.Status = status;
            log($"Template \"{template.Name}\" updated ({seconds:0.#} s).", false);
            dirty = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            template.Status = "Stopped.";
        }
        catch (CaptureException error)
        {
            template.Status = $"Screenshot failed {DateTime.Now:HH:mm:ss}: {error.Message}";
            log($"Template \"{template.Name}\" screenshot failed: {error.Message}", true);
        }
        catch (Exception error) when (error is LlmException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            template.Status = $"Failed {DateTime.Now:HH:mm:ss}: {Explain(error)}" + (template.AutoUpdate ? $" Retrying in {template.IntervalSeconds} s." : "");
            log($"Template \"{template.Name}\" failed: {Explain(error)}", true);
        }
        finally
        {
            running.Remove(template.Id);
            cancellation.Dispose();
            template.IsRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static string BuildSystem(bool tools, bool image = false)
    {
        var text = new StringBuilder();
        text.AppendLine("You are an assistant built into AudioTranscriber, a live transcription app. You receive the transcript of a conversation that may still be in progress.");
        text.AppendLine("The transcript comes from speech recognition: expect misheard words, especially names, and generic speaker labels such as 'Speaker 1'. Infer sensibly and do not invent facts.");
        text.AppendLine("Follow the user's template instructions and reply with only the requested document (Markdown is fine). No preamble, no closing remarks, no questions back.");
        text.AppendLine("Sections titled 'Output of \"…\"' are the latest results of other templates (other focused assistants); treat them as inputs.");
        if (image)
            text.AppendLine("You also get a screenshot of the user's virtual tabletop (for example Roll20 or Foundry VTT), taken just now. Only report what is actually visible in it; if something is unreadable or not shown, say so instead of guessing.");
        if (tools)
            text.AppendLine("You can call list_files, read_file and search_files to consult the user's reference files (rules, adventure books, notes). Search for names, places and topics from the transcript and use what you find; cite file and page when helpful. Keep tool use focused.");
        return text.ToString();
    }

    private static string BuildUser(OutputTemplate template, string? transcript, IReadOnlyList<(string Name, string Output)> inputs, string references, string previous)
    {
        var text = new StringBuilder();
        text.AppendLine($"Current local time: {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
        if (references.Length > 0) text.AppendLine("## Reference files (always included)").AppendLine(references).AppendLine();
        if (transcript is not null) text.AppendLine("## Transcript so far").AppendLine(Tail(transcript, template.MaxTranscriptChars)).AppendLine();
        foreach (var (name, output) in inputs)
            text.AppendLine($"## Output of \"{name}\"").AppendLine(string.IsNullOrWhiteSpace(output) ? "(no output yet)" : output.Trim()).AppendLine();
        if (previous.Length > 0)
            text.AppendLine("## Your previous output").AppendLine("Update it with what is new in the inputs above; keep what is still correct.")
                .AppendLine(previous).AppendLine();
        text.AppendLine("## Template instructions").AppendLine(template.Prompt.Trim());
        return text.ToString();
    }

    public static string Tail(string transcript, int maxChars)
    {
        if (transcript.Length <= maxChars) return transcript;
        var start = transcript.Length - maxChars;
        var lineBreak = transcript.IndexOf('\n', start);
        if (lineBreak > 0 && lineBreak < transcript.Length - 1) start = lineBreak + 1;
        return "[…earlier transcript omitted…]\n" + transcript[start..];
    }
}
