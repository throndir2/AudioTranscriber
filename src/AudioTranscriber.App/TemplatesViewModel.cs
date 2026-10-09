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
    private LlmConnection? selectedConnection, defaultConnection;
    private OutputTemplate? selectedTemplate;
    private TemplateTreeNode? selectedNode;
    private string selectedFolder = "", listStatus = "";
    private readonly HashSet<string> collapsedFolders = new(StringComparer.OrdinalIgnoreCase);
    private bool treeQueued, selectedFavorites;
    private const string FavoritesKey = "\u0001favorites";
    private string tablePath = "";
    private string? tableText;
    private DateTime tableStamp;
    private char tableSeparator = TemplateTable.DefaultSeparator;
    private bool tablePending;
    private const int CurrentLibraryVersion = 1;
    private string contextFolder = "", pinnedFiles = "", connectionStatus = "", targetDescription = "No session selected.";
    private string captureTarget = "", captureCaption = "No screenshot taken yet.";
    private int captureMaxWidth = ScreenCapture.DefaultMaxWidth;
    private Avalonia.Media.Imaging.Bitmap? capturePreview;
    private bool dirty, ticking, closing, loadingModels, capturing, updatingChoices;

    public TemplatesViewModel(IAppController controller, DesktopDialogs dialogs, Dispatcher dispatcher, Func<Guid?> targetSession,
        Action<string, bool> log)
    {
        this.controller = controller;
        this.dialogs = dialogs;
        this.dispatcher = dispatcher;
        this.targetSession = targetSession;
        this.log = log;
        Load();
        AddConnectionCommand = new RelayCommand<string>(AddConnection);
        MakeDefaultCommand = new RelayCommand(() => DefaultConnection = SelectedConnection,
            () => SelectedConnection is not null && SelectedConnection != DefaultConnection);
        RemoveConnectionCommand = new AsyncCommand(RemoveConnectionAsync, () => SelectedConnection is not null);
        LoadModelsCommand = new AsyncCommand(LoadModelsAsync, () => SelectedConnection is not null && !loadingModels);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () => SelectedConnection is not null && !loadingModels);
        CheckImageSupportCommand = new AsyncCommand(CheckImageSupportAsync, () => SelectedConnection is not null && !loadingModels);
        AddTemplateCommand = new RelayCommand(() => AddTemplate(new OutputTemplate { Name = "New template", Folder = selectedFolder, IsFavorite = selectedFavorites, Prompt = "Describe what to produce from the transcript…" }));
        DuplicateTemplateCommand = new RelayCommand(DuplicateTemplate, () => SelectedTemplate is not null);
        DeleteTemplateCommand = new AsyncCommand(DeleteTemplateAsync, () => SelectedTemplate is not null);
        RestoreBuiltInsCommand = new RelayCommand(RestoreBuiltIns);
        AddTableStartersCommand = new RelayCommand(AddTableStarters);
        OpenTableCommand = new RelayCommand(OpenTable);
        BrowseTableCommand = new AsyncCommand(async () => { if (await dialogs.ChooseTemplateTableAsync(TablePath) is { } path) TablePath = path; });
        OpenVersionsCommand = new RelayCommand(OpenVersions, () => SelectedTemplate is not null);
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
        Templates.CollectionChanged += (_, _) => { RebuildInputOptions(); QueueTreeRebuild(); };
        Models.CollectionChanged += (_, _) => { Changed(nameof(HasModels)); Changed(nameof(ModelPickerText)); };
        RebuildInputOptions();
        SyncChoices();
        RebuildTree();
    }

    private static DispatcherTimer NewTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    public ObservableCollection<LlmConnection> Connections { get; } = [];
    /// <summary>The selected template's connection picker: "Default (…)" first, then every connection.</summary>
    public ObservableCollection<ConnectionChoice> ConnectionChoices { get; } = [];
    public ObservableCollection<OutputTemplate> Templates { get; } = [];
    /// <summary>Checklist of the other templates the selected template can use as inputs.</summary>
    public ObservableCollection<TemplateInputOption> TemplateInputs { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<string> CaptureSources { get; } = [];
    public ICommand RefreshCaptureSourcesCommand { get; }
    public ICommand TestCaptureCommand { get; }

    public ICommand AddConnectionCommand { get; }
    public ICommand MakeDefaultCommand { get; }
    public ICommand RemoveConnectionCommand { get; }
    public ICommand LoadModelsCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand CheckImageSupportCommand { get; }
    public ICommand AddTemplateCommand { get; }
    public ICommand DuplicateTemplateCommand { get; }
    public ICommand DeleteTemplateCommand { get; }
    public ICommand RestoreBuiltInsCommand { get; }
    public ICommand AddTableStartersCommand { get; }
    public ICommand OpenTableCommand { get; }
    public ICommand BrowseTableCommand { get; }
    public ICommand OpenVersionsCommand { get; }

    /// <summary>Templates grouped by folder; rebuilt when templates are added, removed or moved.</summary>
    public ObservableCollection<TemplateTreeNode> TemplateTree { get; } = [];
    /// <summary>Every folder path in use, for the folder picker.</summary>
    public ObservableCollection<string> FolderNames { get; } = [];
    public string ListStatus { get => listStatus; private set => Set(ref listStatus, value); }

    public TemplateTreeNode? SelectedNode
    {
        get => selectedNode;
        set
        {
            // The tree briefly selects nothing while it is rebuilt; keep the current template then.
            if (value is null || !Set(ref selectedNode, value)) return;
            selectedFolder = value.Template?.Folder ?? value.FolderPath;
            selectedFavorites = value.IsFavorites;
            SelectedTemplate = value.Template;
        }
    }

    /// <summary>The CSV table that mirrors the template list. Pointing at an existing table loads it; otherwise it is created.</summary>
    public string TablePath
    {
        get => tablePath;
        set
        {
            var path = string.IsNullOrWhiteSpace(value) ? DefaultTablePath : value.Trim().Trim('"');
            if (!Set(ref tablePath, path)) return;
            tableText = null;
            tableStamp = default;
            dirty = true;
            var existed = File.Exists(path);
            ReadTableIfChanged();
            Save();
            if (!existed && !tablePending) ListStatus = $"Created the table with your {Templates.Count} templates.";
        }
    }
    public ICommand RunTemplateCommand { get; }
    public ICommand StopTemplateCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand AddPinnedFilesCommand { get; }
    public ICommand CopyOutputCommand { get; }

    public LlmConnection? SelectedConnection
    {
        get => selectedConnection;
        set
        {
            if (!Set(ref selectedConnection, value)) return;
            Models.Clear();
            ConnectionStatus = "";
            Changed(nameof(HasConnection));
            Changed(nameof(HasNoConnection));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The connection templates use unless they pick their own.</summary>
    public LlmConnection? DefaultConnection
    {
        get => defaultConnection;
        set
        {
            if (!Set(ref defaultConnection, value)) return;
            foreach (var connection in Connections) connection.IsDefault = connection == value;
            RefreshChoiceLabels();
            dirty = true;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The selected template's connection; <see cref="Guid.Empty"/> means the default connection.</summary>
    public Guid SelectedTemplateConnectionId
    {
        get => SelectedTemplate?.ConnectionId ?? Guid.Empty;
        set
        {
            if (updatingChoices || SelectedTemplate is not { } template) return;
            Guid? id = value == Guid.Empty ? null : value;
            if (template.ConnectionId == id) return;
            template.ConnectionId = id;
            Changed();
        }
    }

    /// <summary>Picking from the loaded list fills in the connection's model.</summary>
    public string? PickedModel
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && SelectedConnection is { } connection) connection.Model = value; Changed(); }
    }
    public bool HasModels => Models.Count > 0;
    public string ModelPickerText => $"Choose one of {Models.Count:N0} models…";
    public bool HasConnection => SelectedConnection is not null;
    public bool HasNoConnection => Connections.Count == 0;
    public string ConnectionStatus { get => connectionStatus; private set => Set(ref connectionStatus, value); }

    public OutputTemplate? SelectedTemplate
    {
        get => selectedTemplate;
        set
        {
            if (!Set(ref selectedTemplate, value)) return;
            Changed(nameof(HasTemplate));
            Changed(nameof(SelectedTemplateConnectionId));
            RebuildInputOptions();
            if (value is not null && selectedNode?.Template != value) SelectNode(value);
        }
    }
    public bool HasTemplate => SelectedTemplate is not null;

    // ---------- folder tree ----------

    private void QueueTreeRebuild()
    {
        if (treeQueued) return;
        treeQueued = true;
        dispatcher.Post(() => { treeQueued = false; RebuildTree(); });
    }

    private void RebuildTree()
    {
        var top = new List<TemplateTreeNode>();
        var folders = new Dictionary<string, TemplateTreeNode>(StringComparer.OrdinalIgnoreCase);
        List<TemplateTreeNode> ChildrenOf(string path)
        {
            if (path.Length == 0) return top;
            if (folders.TryGetValue(path, out var existing)) return existing.Children;
            var slash = path.LastIndexOf('/');
            var parent = ChildrenOf(slash < 0 ? "" : path[..slash]);
            var node = new TemplateTreeNode(path[(slash + 1)..], path, null) { IsExpanded = !collapsedFolders.Contains(path) };
            node.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TemplateTreeNode.IsExpanded)) return;
                if (node.IsExpanded) collapsedFolders.Remove(path); else collapsedFolders.Add(path);
            };
            folders[path] = node;
            parent.Add(node);
            return node.Children;
        }
        foreach (var template in Templates) ChildrenOf(template.Folder).Add(new TemplateTreeNode(template.Name, template.Folder, template));

        // Folders first (alphabetical), then templates in list order.
        static void Sort(List<TemplateTreeNode> nodes)
        {
            var sorted = nodes.OrderBy(n => n.IsFolder ? 0 : 1).ThenBy(n => n.IsFolder ? n.Name : "", StringComparer.CurrentCultureIgnoreCase).ToList();
            nodes.Clear();
            nodes.AddRange(sorted);
            foreach (var folder in nodes.Where(n => n.IsFolder)) Sort(folder.Children);
        }
        Sort(top);

        // Favorites stay in their own folder too; the group at the top is a shortcut to them.
        if (Templates.Any(t => t.IsFavorite))
        {
            var favorites = new TemplateTreeNode("★ Favorites", "", null, isFavorites: true) { IsExpanded = !collapsedFolders.Contains(FavoritesKey) };
            favorites.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TemplateTreeNode.IsExpanded)) return;
                if (favorites.IsExpanded) collapsedFolders.Remove(FavoritesKey); else collapsedFolders.Add(FavoritesKey);
            };
            foreach (var template in Templates.Where(t => t.IsFavorite))
                favorites.Children.Add(new TemplateTreeNode(template.Name, template.Folder, template, isFavorites: true));
            top.Insert(0, favorites);
        }

        var previousFolder = selectedNode is { IsFolder: true } ? selectedNode : null;
        selectedNode = null;
        TemplateTree.Clear();
        foreach (var node in top) TemplateTree.Add(node);
        var folderNames = folders.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!folderNames.SequenceEqual(FolderNames))
        {
            FolderNames.Clear();
            foreach (var path in folderNames) FolderNames.Add(path);
        }
        if (SelectedTemplate is { } selected) SelectNode(selected);
        else if (previousFolder is not null && TemplateTree.SelectMany(n => n.All()).FirstOrDefault(n => n.IsFolder &&
                     n.IsFavorites == previousFolder.IsFavorites && n.FolderPath.Equals(previousFolder.FolderPath, StringComparison.OrdinalIgnoreCase)) is { } folderNode)
        {
            selectedNode = folderNode;
            Changed(nameof(SelectedNode));
        }
        else Changed(nameof(SelectedNode));
    }

    private void SelectNode(OutputTemplate template)
    {
        // Prefer the copy in the group the user was working in (Favorites or the template's folder).
        var nodes = TemplateTree.SelectMany(n => n.All()).Where(n => n.Template == template).ToList();
        var node = nodes.FirstOrDefault(n => n.IsFavorites == selectedFavorites) ?? nodes.FirstOrDefault();
        if (node is null) return;
        if (node.IsFavorites)
        {
            collapsedFolders.Remove(FavoritesKey);
            TemplateTree.First(n => n.IsFavorites).IsExpanded = true;
        }
        // Open the folders above it so the selection is visible.
        var path = node.IsFavorites ? "" : template.Folder;
        while (path.Length > 0)
        {
            collapsedFolders.Remove(path);
            if (TemplateTree.SelectMany(n => n.All()).FirstOrDefault(n => n.IsFolder && !n.IsFavorites && n.FolderPath.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } folder)
                folder.IsExpanded = true;
            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }
        selectedNode = node;
        selectedFolder = template.Folder;
        selectedFavorites = node.IsFavorites;
        Changed(nameof(SelectedNode));
    }

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
                Version = TemplateSettings.CurrentVersion,
                Connections = [new LlmConnection { Name = ollama.Name, Kind = ollama.Name, BaseUrl = ollama.BaseUrl, Model = ollama.Model }],
                Templates = OutputTemplate.BuiltIns().ToList(),
                LibraryVersion = CurrentLibraryVersion
            };
            dirty = true;
            freshDefaults = true;
        }
        if (saved.Version < 1)
        {
            // 60,000 was the old default size; those templates now fill the model's context window instead.
            foreach (var template in saved.Templates.Where(t => t.MaxTranscriptChars == 60000)) template.MaxTranscriptChars = 0;
            dirty = true;
        }
        contextFolder = saved.ContextFolder ?? "";
        pinnedFiles = saved.PinnedFiles ?? "";
        captureTarget = saved.CaptureTarget ?? "";
        captureMaxWidth = saved.CaptureMaxWidth > 0 ? Math.Clamp(saved.CaptureMaxWidth, 320, 7680) : ScreenCapture.DefaultMaxWidth;
        foreach (var connection in saved.Connections) TrackConnection(connection);
        foreach (var template in saved.Templates) Track(template, Templates);
        defaultConnection = Connections.FirstOrDefault(c => c.Id == saved.DefaultConnectionId) ?? Connections.FirstOrDefault();
        foreach (var connection in Connections) connection.IsDefault = connection == defaultConnection;
        // Older files pinned every template, mostly to the first connection by accident; those now follow the default.
        var legacy = saved.DefaultConnectionId is null;
        foreach (var template in Templates.Where(t => t.ConnectionId is { } id &&
                     (Connections.All(c => c.Id != id) || legacy && id == defaultConnection?.Id)))
            template.ConnectionId = null;
        if (legacy) dirty = true;
        selectedConnection = defaultConnection;
        tablePath = string.IsNullOrWhiteSpace(saved.TablePath) ? DefaultTablePath : saved.TablePath;
        // The table wins over templates.json: it may have been edited while the app was closed.
        ReadTableIfChanged();
        if (saved.LibraryVersion < 1)
        {
            // Lists from before folders and the TTRPG set: file built-in templates in their folders and add the TTRPG set once.
            foreach (var template in Templates.Where(t => t.Folder.Length == 0)) template.Folder = OutputTemplate.BuiltInFolder(template.Name) ?? "";
            AddMissing(OutputTemplate.TtrpgTemplates());
            dirty = true;
        }
        if (!File.Exists(TablePath)) dirty = true;
        selectedTemplate = Templates.FirstOrDefault();
    }

    private void Track<T>(T item, ObservableCollection<T> list) where T : INotifyPropertyChanged
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(OutputTemplate.Status) or nameof(OutputTemplate.IsRunning) or nameof(OutputTemplate.Summary))) dirty = true;
            if (item is OutputTemplate && e.PropertyName is nameof(OutputTemplate.Folder) or nameof(OutputTemplate.IsFavorite)) QueueTreeRebuild();
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
                Version = TemplateSettings.CurrentVersion,
                Connections = Connections.ToList(),
                DefaultConnectionId = DefaultConnection?.Id,
                Templates = Templates.ToList(),
                ContextFolder = ContextFolder,
                PinnedFiles = PinnedFiles,
                CaptureTarget = CaptureTarget,
                CaptureMaxWidth = CaptureMaxWidth,
                TablePath = TablePath.Equals(DefaultTablePath, StringComparison.OrdinalIgnoreCase) ? "" : TablePath,
                LibraryVersion = CurrentLibraryVersion
            };
            var path = SettingsPath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { dirty = true; }
        WriteTable();
    }

    // ---------- template table (CSV) ----------

    private string DefaultTablePath => Path.Combine(controller.Store.RootDirectory, TemplateTable.FileName);

    /// <summary>Writes the table when the list changed. While another program holds the file, retries on each tick.</summary>
    private void WriteTable()
    {
        var path = TablePath;
        var text = TemplateTable.Write(Templates, Connections, tableSeparator);
        if (text == tableText && File.Exists(path)) { tablePending = false; return; }
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder) Directory.CreateDirectory(folder);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                writer.Write(text);
            tableText = text;
            tableStamp = File.GetLastWriteTimeUtc(path);
            if (tablePending) ListStatus = $"Table updated {DateTime.Now:HH:mm:ss}.";
            tablePending = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!tablePending) ListStatus = "Can't write the table now (is it open in a spreadsheet app?). Changes are saved here and written to the table when it is free.";
            tablePending = true;
        }
    }

    /// <summary>Loads the table when it changed on disk since the app last read or wrote it. The table wins over unsaved edits here.</summary>
    private void ReadTableIfChanged()
    {
        var path = TablePath;
        try
        {
            if (!File.Exists(path)) return;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == tableStamp && tableText is not null) return;
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            tableStamp = stamp;
            var text = TemplateTable.Decode(bytes);
            if (text == tableText) return;
            var rows = TemplateTable.Read(text, out tableSeparator);
            if (rows.Count == 0 && Templates.Count > 0)
            {
                // An empty table is more likely a half-saved file than a wish to delete everything.
                ListStatus = "The table has no templates, so it was not loaded. It is rewritten from the list here.";
                tableText = null;
                dirty = true;
                return;
            }
            ApplyTable(rows);
            tableText = text;
            ListStatus = $"Loaded {rows.Count} templates from the table {DateTime.Now:HH:mm:ss}.";
        }
        catch (FormatException error)
        {
            ListStatus = "Could not read the table: " + error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private void ApplyTable(IReadOnlyList<Dictionary<string, string>> rows)
    {
        var next = TemplateTable.Apply(rows, Templates.ToList(), Connections);
        foreach (var gone in Templates.Except(next).ToList())
        {
            if (running.TryGetValue(gone.Id, out var cancellation)) cancellation.Cancel();
            Templates.Remove(gone);
        }
        for (var i = 0; i < next.Count; i++)
        {
            var index = Templates.IndexOf(next[i]);
            if (index < 0)
            {
                Track(next[i], Templates);
                index = Templates.Count - 1;
            }
            if (index != i) Templates.Move(index, i);
        }
        if (SelectedTemplate is { } selected && !Templates.Contains(selected)) SelectedTemplate = Templates.FirstOrDefault();
        RebuildInputOptions();
        Changed(nameof(SelectedTemplateConnectionId));
        dirty = true;
    }

    private void OpenTable()
    {
        Save();
        try { AppDiagnostics.OpenFile(TablePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ListStatus = "Could not open the table: " + error.Message;
        }
    }

    // ---------- connections ----------

    private void TrackConnection(LlmConnection connection)
    {
        connection.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LlmConnection.Name)) RefreshChoiceLabels(); };
        Track(connection, Connections);
    }

    // Connections are only appended or removed, so the picker keeps its items instead of being rebuilt.
    private void SyncChoices()
    {
        updatingChoices = true;
        try
        {
            if (ConnectionChoices.Count == 0) ConnectionChoices.Add(new ConnectionChoice(Guid.Empty));
            foreach (var stale in ConnectionChoices.Where(c => c.Id != Guid.Empty && Connections.All(x => x.Id != c.Id)).ToList())
                ConnectionChoices.Remove(stale);
            foreach (var added in Connections.Where(c => ConnectionChoices.All(x => x.Id != c.Id)).ToList())
                ConnectionChoices.Add(new ConnectionChoice(added.Id));
            RefreshChoiceLabels();
        }
        finally { updatingChoices = false; }
        Changed(nameof(SelectedTemplateConnectionId));
        Changed(nameof(HasNoConnection));
    }

    private void RefreshChoiceLabels()
    {
        foreach (var choice in ConnectionChoices)
            choice.Label = choice.Id == Guid.Empty
                ? DefaultConnection is { } fallback ? $"Default ({fallback.Name})" : "Default"
                : Connections.FirstOrDefault(c => c.Id == choice.Id)?.Name ?? "";
    }

    private void AddConnection(string? kind)
    {
        var preset = LlmPreset.All.FirstOrDefault(p => p.Name == kind) ?? LlmPreset.All[^1];
        var connection = new LlmConnection { Name = UniqueName(preset.Name), Kind = preset.Name, BaseUrl = preset.BaseUrl, Model = preset.Model };
        TrackConnection(connection);
        SyncChoices();
        DefaultConnection = connection;
        SelectedConnection = connection;
        dirty = true;
        var pinned = Templates.Count(t => t.ConnectionId is not null);
        ConnectionStatus = "Added. Templates now use it by default" +
            (pinned > 0 ? $" (except {pinned} that pick their own connection)" : "") + ". " +
            (preset.NeedsKey ? "Paste the API key and choose Save key, then Load models." : "Choose Load models to see what the server offers.");
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
        if (!await dialogs.ConfirmAsync("Remove connection",
                $"Remove the LLM connection \"{connection.Name}\" and its saved API key? Templates that use it switch to the default connection.")) return;
        foreach (var template in Templates.Where(t => t.ConnectionId == connection.Id)) template.ConnectionId = null;
        Changed(nameof(SelectedTemplateConnectionId));
        Connections.Remove(connection);
        SyncChoices();
        if (DefaultConnection == connection) DefaultConnection = Connections.FirstOrDefault();
        SelectedConnection = DefaultConnection;
        Save();
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
            await CheckContextAsync(connection);
            ConnectionStatus += " " + connection.ContextStatus;
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

    private async Task CheckContextAsync(LlmConnection connection)
    {
        var key = connection.ContextKey;
        var window = await LlmClient.ContextWindowAsync(connection.BaseUrl, connection.GetKey(), connection.Model, CancellationToken.None);
        if (key != connection.ContextKey) return;
        connection.SetContextWindow(key, window);
        dirty = true;
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
        SelectedTemplate = template;
        dirty = true;
    }

    /// <summary>Adds the templates whose names are not in the list yet; returns how many were added.</summary>
    private int AddMissing(IEnumerable<OutputTemplate> templates)
    {
        var added = 0;
        foreach (var template in templates.Where(t => !Templates.Any(e => e.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))))
        {
            Track(template, Templates);
            added++;
        }
        if (added == 0) return 0;
        dirty = true;
        return added;
    }

    private void RestoreBuiltIns()
    {
        var added = AddMissing(OutputTemplate.BuiltIns());
        ListStatus = added == 0 ? "All built-in templates are already in the list." : $"Added {added} built-in template{(added == 1 ? "" : "s")}.";
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
            Name = source.Name + " (copy)", Folder = source.Folder, Prompt = source.Prompt, ConnectionId = source.ConnectionId, IntervalSeconds = source.IntervalSeconds,
            IncludePrevious = source.IncludePrevious, UseReferences = source.UseReferences, MaxTranscriptChars = source.MaxTranscriptChars,
            UseTranscript = source.UseTranscript, IncludeTimestamps = source.IncludeTimestamps, InputTemplateIds = [.. source.InputTemplateIds], UseScreenshot = source.UseScreenshot,
            KeepVersions = source.KeepVersions, MaxVersions = source.MaxVersions
        });
    }

    private void OpenVersions()
    {
        if (SelectedTemplate is not { } template) return;
        try { AppDiagnostics.OpenFolder(TemplateVersions.Folder(template, controller.Store.RootDirectory)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            template.Status = "Could not open the versions folder: " + error.Message;
        }
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
        Connections.FirstOrDefault(c => c.Id == template.ConnectionId) ?? DefaultConnection ?? Connections.FirstOrDefault();

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
            ReadTableIfChanged();
            if (dirty || tablePending) Save();
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
        if (ConnectionFor(template) is not { } connection) { template.Status = "Add an LLM connection in Privacy / models first."; return; }
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
            if (!connection.ContextChecked)
            {
                template.Status = "Checking the model's context window…";
                await CheckContextAsync(connection);
            }
            var system = BuildSystem(tools is not null, images is not null);
            var included = !template.UseTranscript || transcript is null ? null
                : template.IncludeTimestamps ? transcript : TranscriptPresentation.WithoutTimestamps(transcript);
            var namedInputs = inputs.Select(i => (i.Name, i.Output)).ToArray();
            var window = connection.ContextTokens;
            var imageTokens = (images?.Count ?? 0) * ContextBudget.ImageTokens;
            var maxChars = template.MaxTranscriptChars > 0 ? template.MaxTranscriptChars : 60000;
            if (template.MaxTranscriptChars == 0 && included is not null && window > 0)
            {
                var fixedTokens = ContextBudget.EstimateTokens(system) + imageTokens +
                    ContextBudget.EstimateTokens(BuildUser(template, "", namedInputs, references, previous, 0));
                var room = window - ContextBudget.OutputReserve(window) - (tools is not null ? ContextBudget.ToolReserve(window) : 0) - fixedTokens;
                maxChars = ContextBudget.TailChars(included, Math.Max(room, 250));
            }
            var user = BuildUser(template, included, namedInputs, references, previous, maxChars);
            var promptTokens = ContextBudget.EstimateTokens(system) + ContextBudget.EstimateTokens(user) + imageTokens;
            var numCtx = 0;
            if (connection.UsesOllamaApi)
            {
                var needed = promptTokens + 4096 + (tools is not null ? 8192 : 0);
                numCtx = connection.OllamaContextInUse = ContextBudget.OllamaContext(connection.OllamaContextInUse, needed, window);
            }
            var progress = new Progress<string>(message => template.Status = message);
            var key = connection.GetKey();
            var result = await Task.Run(() => LlmClient.CompleteAsync(connection.BaseUrl, key, connection.Model, system, user, tools, progress, cancellation.Token, images, numCtx));
            if (string.IsNullOrWhiteSpace(result)) throw new LlmException("The model returned an empty answer.");
            template.Output = result;
            template.OutputSessionId = template.UseTranscript ? session : null;
            template.LastFingerprint = fingerprint;
            var seconds = (DateTime.Now - started).TotalSeconds;
            var status = $"Updated {DateTime.Now:HH:mm:ss} in {seconds:0.#} s · {connection.Name} · {connection.Model}";
            status += window > 0 ? $" · about {promptTokens:N0} of {window:N0} tokens" : $" · about {promptTokens:N0} tokens";
            if (included is not null && included.Length > maxChars)
                status += $" · transcript cut: only the last {maxChars:N0} of {included.Length:N0} characters were sent; " +
                          (template.MaxTranscriptChars > 0 ? "raise Transcript chars, or set it to 0 to fill the model's context window"
                           : window > 0 ? "the model's context window is full (untick timestamps to fit more)"
                           : "the model's context window is unknown, so 60,000 were sent; type the model's Token limit on the connection");
            if (template.WriteToFile && template.OutputPath.Trim() is { Length: > 0 } path)
            {
                var bytes = await Task.Run(() => LiveTranscriptFile.Write(path, result + Environment.NewLine));
                status += $" · {bytes:N0} bytes → {path}";
            }
            if (template.KeepVersions)
            {
                var root = controller.Store.RootDirectory;
                try
                {
                    if (await Task.Run(() => TemplateVersions.Save(template, root, result + Environment.NewLine, DateTime.Now)) is { } version)
                        status += $" · version saved: {Path.GetFileName(version)}";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    status += " · version not saved: " + error.Message;
                }
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

    private static string BuildUser(OutputTemplate template, string? transcript, IReadOnlyList<(string Name, string Output)> inputs, string references,
        string previous, int maxTranscriptChars)
    {
        // Parts that change on every run come last, so servers can reuse the processed start of a long prompt.
        var text = new StringBuilder();
        if (references.Length > 0) text.AppendLine("## Reference files (always included)").AppendLine(references).AppendLine();
        if (transcript is not null) text.AppendLine("## Transcript so far").AppendLine(Tail(transcript, maxTranscriptChars)).AppendLine();
        foreach (var (name, output) in inputs)
            text.AppendLine($"## Output of \"{name}\"").AppendLine(string.IsNullOrWhiteSpace(output) ? "(no output yet)" : output.Trim()).AppendLine();
        if (previous.Length > 0)
            text.AppendLine("## Your previous output").AppendLine("Update it with what is new in the inputs above; keep what is still correct.")
                .AppendLine(previous).AppendLine();
        text.AppendLine($"Current local time: {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
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
