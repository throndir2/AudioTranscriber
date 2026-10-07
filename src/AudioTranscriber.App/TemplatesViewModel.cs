using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
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
    private bool dirty, ticking, closing, loadingModels;

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
        RemoveConnectionCommand = new RelayCommand(RemoveConnection, () => SelectedConnection is not null);
        LoadModelsCommand = new AsyncCommand(LoadModelsAsync, () => SelectedConnection is not null && !loadingModels);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () => SelectedConnection is not null && !loadingModels);
        CheckImageSupportCommand = new AsyncCommand(CheckImageSupportAsync, () => SelectedConnection is not null && !loadingModels);
        AddTemplateCommand = new RelayCommand(() => AddTemplate(new OutputTemplate { Name = "New template", Prompt = "Describe what to produce from the transcript…" }));
        DuplicateTemplateCommand = new RelayCommand(DuplicateTemplate, () => SelectedTemplate is not null);
        DeleteTemplateCommand = new RelayCommand(DeleteTemplate, () => SelectedTemplate is not null);
        AddStartersCommand = new RelayCommand(() => { foreach (var t in OutputTemplate.Starters()) AddTemplate(t); });
        RunTemplateCommand = new RelayCommand(() => { if (SelectedTemplate is { } t) _ = RunAsync(t, manual: true); },
            () => SelectedTemplate is { IsRunning: false });
        StopTemplateCommand = new RelayCommand(() => { if (SelectedTemplate is { } t && running.TryGetValue(t.Id, out var c)) c.Cancel(); },
            () => SelectedTemplate is { IsRunning: true });
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => SelectedTemplate is not null);
        BrowseFolderCommand = new RelayCommand(() => { if (dialogs.ChooseFolder(ContextFolder) is { } folder) ContextFolder = folder; });
        AddPinnedFilesCommand = new RelayCommand(AddPinnedFiles);
        CopyOutputCommand = new RelayCommand(() =>
        {
            try { System.Windows.Clipboard.SetText(SelectedTemplate?.Output ?? ""); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }, () => !string.IsNullOrEmpty(SelectedTemplate?.Output));
        timer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => _ = TickAsync(), dispatcher);
        timer.Stop();
    }

    public ObservableCollection<LlmConnection> Connections { get; } = [];
    public ObservableCollection<OutputTemplate> Templates { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public IReadOnlyList<LlmPreset> Presets => LlmPreset.All;

    public ICommand AddConnectionCommand { get; }
    public ICommand RemoveConnectionCommand { get; }
    public ICommand LoadModelsCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand CheckImageSupportCommand { get; }
    public ICommand AddTemplateCommand { get; }
    public ICommand DuplicateTemplateCommand { get; }
    public ICommand DeleteTemplateCommand { get; }
    public ICommand AddStartersCommand { get; }
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
        set { if (Set(ref selectedTemplate, value)) Changed(nameof(HasTemplate)); }
    }
    public bool HasTemplate => SelectedTemplate is not null;

    public string ContextFolder { get => contextFolder; set { if (Set(ref contextFolder, value?.Trim() ?? "")) dirty = true; } }
    public string PinnedFiles { get => pinnedFiles; set { if (Set(ref pinnedFiles, value ?? "")) dirty = true; } }
    public string TargetDescription { get => targetDescription; private set => Set(ref targetDescription, value); }

    public void Start() { timer.Start(); _ = TickAsync(); }

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
        ConnectionStatus = string.IsNullOrWhiteSpace(key) ? "API key removed." : "API key saved, encrypted for your Windows account.";
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
        }
        contextFolder = saved.ContextFolder ?? "";
        pinnedFiles = saved.PinnedFiles ?? "";
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
                PinnedFiles = PinnedFiles
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

    private void RemoveConnection()
    {
        if (SelectedConnection is not { } connection) return;
        if (!dialogs.Confirm("Remove connection", $"Remove the LLM connection \"{connection.Name}\" and its saved API key?")) return;
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

    private void DuplicateTemplate()
    {
        if (SelectedTemplate is not { } source) return;
        AddTemplate(new OutputTemplate
        {
            Name = source.Name + " (copy)", Prompt = source.Prompt, ConnectionId = source.ConnectionId, IntervalSeconds = source.IntervalSeconds,
            IncludePrevious = source.IncludePrevious, UseReferences = source.UseReferences, MaxTranscriptChars = source.MaxTranscriptChars
        });
    }

    private void DeleteTemplate()
    {
        if (SelectedTemplate is not { } template) return;
        if (!dialogs.Confirm("Delete template", $"Delete the template \"{template.Name}\"? Its output file, if any, is left in place.")) return;
        if (running.TryGetValue(template.Id, out var cancellation)) cancellation.Cancel();
        Templates.Remove(template);
        SelectedTemplate = Templates.FirstOrDefault();
        Save();
    }

    private void BrowseOutput()
    {
        if (SelectedTemplate is not { } template) return;
        if (dialogs.SaveTemplateOutput(template.OutputPath, template.Name) is { } path) { template.OutputPath = path; template.WriteToFile = true; }
    }

    private void AddPinnedFiles()
    {
        var files = dialogs.ChooseReferenceFiles(ContextFolder);
        if (files.Count == 0) return;
        var existing = PinnedList();
        var added = files.Where(f => !existing.Contains(f, StringComparer.OrdinalIgnoreCase)).ToArray();
        PinnedFiles = string.Join(Environment.NewLine, existing.Concat(added));
    }

    private string[] PinnedList() => PinnedFiles.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.Trim('"')).Where(p => p.Length > 0).ToArray();

    private LlmConnection? ConnectionFor(OutputTemplate template) =>
        Connections.FirstOrDefault(c => c.Id == template.ConnectionId) ?? Connections.FirstOrDefault();

    // ---------- running ----------

    private async Task TickAsync()
    {
        if (ticking || closing) return;
        ticking = true;
        try
        {
            if (dirty) Save();
            var target = targetSession();
            if (target is not { } id) { TargetDescription = "No session selected. Templates run on the session being recorded, or the session selected on the left."; return; }
            var store = controller.Store;
            string name;
            try { name = store.GetSession(id).Name; }
            catch (InvalidOperationException) { return; }
            var recording = controller.RecordingSessionId == id;
            TargetDescription = recording ? $"Following the recording: \"{name}\"" : $"Using the selected session: \"{name}\"";
            var due = Templates.Where(t => t.AutoUpdate && !t.IsRunning &&
                DateTime.UtcNow - t.LastRunUtc >= TimeSpan.FromSeconds(t.IntervalSeconds)).ToArray();
            if (due.Length == 0) return;
            var (transcript, rows) = await Task.Run(() => (LiveTranscriptFile.Render(store, id, out var count), count));
            if (rows == 0 || closing) return;
            var fingerprint = Fingerprint(id, transcript);
            foreach (var template in due)
                if (template.LastFingerprint != fingerprint) _ = RunAsync(template, manual: false, id, transcript, fingerprint);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        finally { ticking = false; }
    }

    private static string Fingerprint(Guid session, string transcript) =>
        session.ToString("N") + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(transcript)));

    private async Task RunAsync(OutputTemplate template, bool manual, Guid? sessionId = null, string? transcript = null, string? fingerprint = null)
    {
        if (template.IsRunning || closing) return;
        if (ConnectionFor(template) is not { } connection) { template.Status = "Add an LLM connection below first."; return; }
        var id = sessionId ?? targetSession();
        if (id is not { } session) { template.Status = "Select or record a session first."; return; }
        var cancellation = new CancellationTokenSource();
        running[template.Id] = cancellation;
        template.IsRunning = true;
        template.LastRunUtc = DateTime.UtcNow;
        CommandManager.InvalidateRequerySuggested();
        var started = DateTime.Now;
        try
        {
            if (transcript is null)
            {
                var store = controller.Store;
                transcript = await Task.Run(() => LiveTranscriptFile.Render(store, session));
                fingerprint = Fingerprint(session, transcript);
            }
            var previous = template.IncludePrevious && template.OutputSessionId == session ? template.Output : "";
            var library = template.UseReferences ? new ReferenceLibrary(ContextFolder, PinnedList()) : null;
            var references = library is null ? "" : await Task.Run(() => library.PinnedText(MaxReferenceChars), cancellation.Token);
            var tools = library is { HasFolder: true } ? library : null;
            var system = BuildSystem(tools is not null);
            var user = BuildUser(template, transcript, references, previous);
            var progress = new Progress<string>(message => template.Status = message);
            var key = connection.GetKey();
            var result = await Task.Run(() => LlmClient.CompleteAsync(connection.BaseUrl, key, connection.Model, system, user, tools, progress, cancellation.Token));
            if (string.IsNullOrWhiteSpace(result)) throw new LlmException("The model returned an empty answer.");
            template.Output = result;
            template.OutputSessionId = session;
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

    private static string BuildSystem(bool tools)
    {
        var text = new StringBuilder();
        text.AppendLine("You are an assistant built into AudioTranscriber, a live transcription app. You receive the transcript of a conversation that may still be in progress.");
        text.AppendLine("The transcript comes from speech recognition: expect misheard words, especially names, and generic speaker labels such as 'Speaker 1'. Infer sensibly and do not invent facts.");
        text.AppendLine("Follow the user's template instructions and reply with only the requested document (Markdown is fine). No preamble, no closing remarks, no questions back.");
        if (tools)
            text.AppendLine("You can call list_files, read_file and search_files to consult the user's reference files (rules, adventure books, notes). Search for names, places and topics from the transcript and use what you find; cite file and page when helpful. Keep tool use focused.");
        return text.ToString();
    }

    private static string BuildUser(OutputTemplate template, string transcript, string references, string previous)
    {
        var text = new StringBuilder();
        text.AppendLine($"Current local time: {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
        if (references.Length > 0) text.AppendLine("## Reference files (always included)").AppendLine(references).AppendLine();
        text.AppendLine("## Transcript so far").AppendLine(Tail(transcript, template.MaxTranscriptChars)).AppendLine();
        if (previous.Length > 0)
            text.AppendLine("## Your previous output").AppendLine("Update it with what is new in the transcript; keep what is still correct.")
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
