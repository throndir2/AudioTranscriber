using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace AudioTranscriber.App.Mcp;

/// <summary>UI MCP hooks: launches the real desktop window and drives it through Windows UI Automation.</summary>
public sealed class UiMcpTools(string defaultDataRoot, bool dataRootSpecified)
{
    private readonly Dictionary<string, AutomationElement> refs = new();
    private readonly object gate = new();
    private Process? app;
    private string? appDataRoot;

    public const string Instructions =
        "Drives the real AudioTranscriber window via UI Automation. Start with launch_app (isolated data root by default) or attach_app, " +
        "then snapshot to see controls with [eN] refs. Target controls by ref, or by name (+ control_type). " +
        "click invokes buttons/tabs/checkboxes; set_text fills text boxes (also the 'File name:' box of Open dialogs); " +
        "select_option picks a combo box entry; read_grid reads the transcript/jobs tables; wait_for polls for text; screenshot returns a PNG. " +
        "play_audio plays a file to the output device so the app can record it. Modal dialogs (confirmations, file pickers) appear as extra windows in snapshot.";

    public IReadOnlyList<McpTool> Tools =>
    [
        McpTool.Create("launch_app", "Launch the AudioTranscriber desktop window and wait for it. Uses a fresh isolated data root unless data_root is given.",
            async (args, token) => McpToolResult.Json(await LaunchAsync(args.String("data_root"), args.String("whisper_model"), args.Int("timeout_seconds", 60), token)),
            ("data_root", "string", "Library folder (default: new temp folder)", false),
            ("whisper_model", "string", "Existing ggml model to preselect in a new library, skipping the automatic 1.5 GiB default-model download", false),
            ("timeout_seconds", "integer", "Startup wait (default 60)", false)),
        McpTool.Create("attach_app", "Attach to an already running AudioTranscriber window (by pid or the first AudioTranscriber.App process).",
            args => McpToolResult.Json(Attach(args.Int("pid", 0))), ("pid", "integer", "Process id (optional)", false)),
        McpTool.Create("close_app", "Close the window normally (the app may ask for confirmation while recording/busy). force=true kills the process.",
            async (args, token) => McpToolResult.Json(await CloseAsync(args.Bool("force", false), args.Int("timeout_seconds", 60), token)),
            ("force", "boolean", "Kill instead of closing", false),
            ("timeout_seconds", "integer", "Exit wait (default 60)", false)),
        McpTool.Create("snapshot", "UI Automation tree of all app windows with [eN] refs, names, states and values.",
            args => McpToolResult.Text(Snapshot(args.Int("max_depth", 40), args.Int("max_nodes", 700), args.Int("max_rows", 15))),
            ("max_depth", "integer", "Tree depth limit (default 40)", false),
            ("max_nodes", "integer", "Node limit (default 700)", false),
            ("max_rows", "integer", "Rows shown per list/grid (default 15)", false)),
        McpTool.Create("find", "Find elements by name text and/or control type; returns refs usable by other tools.",
            args => McpToolResult.Json(Find(args).Take(args.Int("limit", 20)).Select(Describe)), TargetParameters("limit", "Maximum results (default 20)")),
        McpTool.Create("click", "Click an element: invokes buttons, selects tabs/list items, toggles checkboxes, expands combos. mouse=true sends a real mouse click; right=true a real right-click (opens context menus).",
            async (args, token) => McpToolResult.Text(await ClickAsync(Resolve(args), args.Bool("mouse", false), args.Bool("double", false), args.Bool("right", false), token)),
            [.. TargetParameters(), ("mouse", "boolean", "Use a real mouse click at the element center", false), ("double", "boolean", "Double-click (implies mouse)", false),
                ("right", "boolean", "Right-click (implies mouse); context menus then appear in snapshot", false)]),
        McpTool.Create("set_text", "Set the text of an edit box (ValuePattern).",
            args =>
            {
                var element = Resolve(args);
                var text = args.String("text") ?? "";
                if (Native.TrySetEditText(new IntPtr(element.Current.NativeWindowHandle), text))
                    return McpToolResult.Text("Set " + Label(element));
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                {
                    ((ValuePattern)pattern).SetValue(text);
                    return McpToolResult.Text("Set " + Label(element));
                }
                throw new InvalidOperationException(Label(element) + " does not accept text.");
            }, [.. TargetParameters(), ("text", "string", "Text to enter", true)]),
        McpTool.Create("select_option", "Select an entry in a combo box or list by (partial) text.",
            async (args, token) => McpToolResult.Text(await SelectOptionAsync(Resolve(args), args.RequireString("option"), token)),
            [.. TargetParameters(), ("option", "string", "Entry text (case-insensitive substring)", true)]),
        McpTool.Create("read_text", "Read the name/value text of an element and its descendants.",
            args => McpToolResult.Text(ReadText(Resolve(args))), TargetParameters()),
        McpTool.Create("read_grid", "Read rows of a data grid (for example name='Transcript lines' or 'Selected session latest transcription jobs').",
            args => McpToolResult.Json(ReadGrid(Resolve(args), args.Int("max_rows", 100))), [.. TargetParameters(), ("max_rows", "integer", "Row limit (default 100)", false)]),
        McpTool.Create("wait_for", "Wait until an element whose name or value contains text appears (or disappears with gone=true).",
            async (args, token) => McpToolResult.Text(await WaitForAsync(args.RequireString("text"), args.Bool("gone", false),
                args.Int("timeout_seconds", 30), token)),
            ("text", "string", "Case-insensitive text to look for", true),
            ("gone", "boolean", "Wait for the text to disappear instead", false),
            ("timeout_seconds", "integer", "Maximum wait (default 30)", false)),
        McpTool.Create("press_key", "Send keys to the app's foreground window: Enter, Escape, Tab, Space, Alt+X, Ctrl+X, or single characters.",
            args => McpToolResult.Text(PressKeys(args.RequireString("keys"))), ("keys", "string", "Key or chord, e.g. 'Enter' or 'Alt+N'", true)),
        McpTool.Create("screenshot", "Capture an app window as PNG (returned inline and saved to disk).",
            args =>
            {
                var (png, path) = Screenshot(args.String("window"), args.String("path"), args.Int("max_width", 1280));
                return new McpToolResult([McpToolResult.ImageBlock(png), McpToolResult.TextBlock("Saved " + path)]);
            },
            ("window", "string", "Window title substring (default: main window)", false),
            ("path", "string", "PNG destination (default: temp file)", false),
            ("max_width", "integer", "Downscale wider captures (default 1280)", false)),
        McpTool.Create("play_audio", "Play a local audio file to a Windows output endpoint so the app can record it. Waits until playback ends.",
            async (args, token) => McpToolResult.Json(await AudioFilePlayer.PlayAsync(args.RequireString("path"),
                args.String("output_device_id"), args.Int("volume_percent", 100) / 100d, token)),
            ("path", "string", "Audio file (wav/mp3)", true),
            ("output_device_id", "string", "Output endpoint id (default: Windows default output)", false),
            ("volume_percent", "integer", "Playback volume 0-100 (default 100)", false)),
        McpTool.Create("app_status", "Process and window status of the driven app.",
            _ => McpToolResult.Json(new
            {
                pid = app?.Id, running = app is { HasExited: false }, exitCode = app is { HasExited: true } ? app.ExitCode : (int?)null,
                dataRoot = appDataRoot, windows = app is { HasExited: false } ? Windows().Select(window => window.Current.Name).ToArray() : []
            })),
    ];

    private static (string, string, string, bool)[] TargetParameters(string? extra = null, string? extraDescription = null)
    {
        var list = new List<(string, string, string, bool)>
        {
            ("ref", "string", "Element ref from the latest snapshot/find, e.g. e12", false),
            ("name", "string", "Element name text (exact match preferred, else substring; access-key underscores ignored)", false),
            ("control_type", "string", "Control type filter, e.g. Button, Edit, ComboBox, TabItem, CheckBox, DataGrid, ListItem, Window", false),
            ("automation_id", "string", "AutomationId filter", false),
            ("index", "integer", "Pick the Nth match (default 0)", false)
        };
        if (extra is not null) list.Add((extra, "integer", extraDescription!, false));
        return [.. list];
    }

    private async Task<object> LaunchAsync(string? dataRoot, string? whisperModel, int timeoutSeconds, CancellationToken token)
    {
        if (app is { HasExited: false }) throw new InvalidOperationException($"The app is already running (pid {app.Id}). close_app first.");
        var root = Path.GetFullPath(dataRoot ?? (dataRootSpecified ? defaultDataRoot
            : Path.Combine(Path.GetTempPath(), "AudioTranscriber-mcp-ui", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6])));
        Directory.CreateDirectory(root);
        var preferences = Path.Combine(root, "preferences.json");
        if (whisperModel is not null && !File.Exists(preferences))
        {
            var model = Path.GetFullPath(whisperModel);
            if (!File.Exists(model)) throw new FileNotFoundException("Whisper model not found: " + model);
            File.WriteAllText(preferences, System.Text.Json.JsonSerializer.Serialize(new { WhisperModelPath = model }));
        }
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown host path.");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(UiMcpTools).Assembly.Location);
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(root);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The app did not start.");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[app] " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[app] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        app = process;
        appDataRoot = root;
        var window = await WaitForMainWindowAsync(timeoutSeconds, token);
        return new { pid = process.Id, dataRoot = root, window = window.Current.Name, windows = Windows().Select(item => item.Current.Name) };
    }

    private object Attach(int pid)
    {
        var process = pid > 0 ? Process.GetProcessById(pid)
            : Process.GetProcessesByName("AudioTranscriber.App").FirstOrDefault() ?? throw new InvalidOperationException("No running AudioTranscriber.App process.");
        app = process;
        appDataRoot = null;
        return new { pid = process.Id, windows = Windows().Select(window => window.Current.Name) };
    }

    private async Task<AutomationElement> WaitForMainWindowAsync(int timeoutSeconds, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (app!.HasExited) throw new InvalidOperationException($"The app exited during startup with code {app.ExitCode}.");
            var windows = Windows();
            var main = windows.FirstOrDefault(window => window.Current.Name.StartsWith("AudioTranscriber ·", StringComparison.Ordinal));
            if (main is not null && main.Current.IsEnabled) return main;
            if (windows.FirstOrDefault(window => window.Current.Name.Contains("startup", StringComparison.OrdinalIgnoreCase)) is { } failure)
                throw new InvalidOperationException("Startup failed: " + ReadText(failure));
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Main window did not appear. Windows: " + string.Join(", ", windows.Select(window => window.Current.Name)));
            await Task.Delay(300, token);
        }
    }

    private async Task<object> CloseAsync(bool force, int timeoutSeconds, CancellationToken token)
    {
        var process = app ?? throw new InvalidOperationException("No app is being driven.");
        if (!process.HasExited)
        {
            if (force) process.Kill(true);
            else if (MainWindow() is { } window && window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                await RunUiAsync(() => ((WindowPattern)pattern).Close(), token);
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(250, token);
            if (!process.HasExited)
                return new { exited = false, windows = Windows().Select(window => window.Current.Name), hint = "A confirmation dialog may be open; snapshot and click its button, or use force." };
        }
        return new { exited = true, exitCode = process.ExitCode, dataRoot = appDataRoot };
    }

    private int Pid => app is { HasExited: false } process ? process.Id
        : throw new InvalidOperationException(app is null ? "No app is being driven. Call launch_app or attach_app." : $"The app exited with code {app.ExitCode}.");

    private List<AutomationElement> Windows()
    {
        var found = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, Pid));
        return found.Cast<AutomationElement>().ToList();
    }

    private AutomationElement? MainWindow() =>
        Windows().FirstOrDefault(window => window.Current.Name.StartsWith("AudioTranscriber ·", StringComparison.Ordinal)) ?? Windows().FirstOrDefault();

    private static CacheRequest Cache()
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.ControlViewCondition };
        foreach (var property in new AutomationProperty[]
                 {
                     AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.AutomationIdProperty,
                     AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.HelpTextProperty,
                     ValuePattern.ValueProperty, ValuePattern.IsReadOnlyProperty, TogglePattern.ToggleStateProperty,
                     SelectionItemPattern.IsSelectedProperty, ExpandCollapsePattern.ExpandCollapseStateProperty
                 })
            cache.Add(property);
        return cache;
    }

    private IEnumerable<(AutomationElement Element, int Depth, int Id, int Parent)> Walk(int maxDepth = 60)
    {
        var cache = Cache();
        var next = 0;
        foreach (var window in Windows())
        {
            AutomationElement cached;
            try { cached = window.GetUpdatedCache(cache); }
            catch (ElementNotAvailableException) { continue; }
            var stack = new Stack<(AutomationElement, int, int)>();
            stack.Push((cached, 0, -1));
            while (stack.Count > 0)
            {
                var (element, depth, parent) = stack.Pop();
                var id = next++;
                yield return (element, depth, id, parent);
                if (depth >= maxDepth) continue;
                var children = element.CachedChildren.Cast<AutomationElement>().ToArray();
                for (var i = children.Length - 1; i >= 0; i--) stack.Push((children[i], depth + 1, id));
            }
        }
    }

    private string Snapshot(int maxDepth, int maxNodes, int maxRows)
    {
        lock (gate) refs.Clear();
        var text = new StringBuilder();
        var count = 0;
        var skipDepth = int.MaxValue;
        var rowCounts = new Dictionary<int, int>();
        var names = new Dictionary<int, string>();
        foreach (var (element, depth, id, parent) in Walk(maxDepth))
        {
            if (depth > skipDepth) continue;
            skipDepth = int.MaxValue;
            var type = TypeName(element);
            if (type is "ScrollBar" or "Thumb" or "Separator") { skipDepth = depth; continue; }
            var normalized = names[id] = Normalize(Cached(element, AutomationElement.NameProperty) as string);
            // Access-key labels repeat their parent's name; empty text nodes add nothing.
            if (type == "Text" && (normalized.Length == 0 || names.GetValueOrDefault(parent) == normalized)) continue;
            if (type is "DataItem" or "ListItem" or "TreeItem")
            {
                var shown = rowCounts.GetValueOrDefault(parent);
                rowCounts[parent] = shown + 1;
                if (shown == maxRows) text.Append(' ', depth * 2).Append("… (more rows; use read_grid/find)\n");
                if (shown >= maxRows) { skipDepth = depth; continue; }
            }
            if (++count > maxNodes) { text.Append($"… node limit {maxNodes} reached\n"); break; }
            text.Append(' ', depth * 2).Append(Describe(element).Line).Append('\n');
        }
        return text.Length == 0 ? "(no windows)" : text.ToString();
    }

    private ElementInfo Describe(AutomationElement element)
    {
        string reference;
        lock (gate)
        {
            reference = "e" + (refs.Count + 1);
            refs[reference] = element;
        }
        var type = TypeName(element);
        var name = Cached(element, AutomationElement.NameProperty) as string ?? "";
        var line = new StringBuilder($"[{reference}] {type}");
        if (name.Length > 0) line.Append(" \"").Append(Truncate(name, 140)).Append('"');
        if (Cached(element, AutomationElement.AutomationIdProperty) is string id && id.Length > 0 && !id.All(char.IsDigit)) line.Append(" #").Append(id);
        if (Cached(element, AutomationElement.IsEnabledProperty) is false) line.Append(" (disabled)");
        if (Cached(element, ValuePattern.ValueProperty) is string value && value.Length > 0 && value != name) line.Append(" value=\"").Append(Truncate(value, 140)).Append('"');
        if (Cached(element, TogglePattern.ToggleStateProperty) is ToggleState toggle) line.Append(toggle == ToggleState.On ? " [checked]" : " [unchecked]");
        if (Cached(element, SelectionItemPattern.IsSelectedProperty) is true) line.Append(" [selected]");
        if (Cached(element, ExpandCollapsePattern.ExpandCollapseStateProperty) is ExpandCollapseState.Expanded) line.Append(" [expanded]");
        if (type is "ComboBox" && element.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection))
        {
            try
            {
                var selected = ((SelectionPattern)selection).Current.GetSelection().FirstOrDefault();
                line.Append(" selected=\"").Append(selected is null ? "" : Truncate(selected.Current.Name, 120)).Append('"');
            }
            catch (ElementNotAvailableException) { }
        }
        return new(reference, type, name, line.ToString());
    }

    private static object? Cached(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCachedPropertyValue(property, true);
            return value == AutomationElement.NotSupported ? null : value;
        }
        catch (InvalidOperationException) { return null; }
    }

    private static string TypeName(AutomationElement element) =>
        (Cached(element, AutomationElement.ControlTypeProperty) as ControlType ?? SafeCurrentType(element))?.ProgrammaticName.Replace("ControlType.", "") ?? "Unknown";

    private static ControlType? SafeCurrentType(AutomationElement element)
    {
        try { return element.Current.ControlType; }
        catch (ElementNotAvailableException) { return null; }
    }

    private static string Truncate(string value, int length)
    {
        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.Length <= length ? value : value[..length] + "…";
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? "").Replace("_", "").Replace("&", "").Replace("…", "...")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private IEnumerable<AutomationElement> Find(McpArguments args)
    {
        var name = Normalize(args.String("name"));
        var type = args.String("control_type");
        var id = args.String("automation_id");
        var candidates = Walk().Select(item => item.Element).Where(element =>
            (type is null || TypeName(element).Equals(type, StringComparison.OrdinalIgnoreCase)) &&
            (id is null || Equals(Cached(element, AutomationElement.AutomationIdProperty), id))).ToList();
        if (name.Length == 0) return candidates;
        bool Matches(AutomationElement element, bool exact)
        {
            var text = Normalize(Cached(element, AutomationElement.NameProperty) as string);
            return exact ? text == name : text.Contains(name, StringComparison.Ordinal) ||
                Normalize(Cached(element, ValuePattern.ValueProperty) as string).Contains(name, StringComparison.Ordinal);
        }
        return candidates.Where(element => Matches(element, true))
            .Concat(candidates.Where(element => !Matches(element, true) && Matches(element, false)))
            .OrderBy(element => Cached(element, AutomationElement.IsOffscreenProperty) is true ? 1 : 0);
    }

    private AutomationElement Resolve(McpArguments args)
    {
        if (args.String("ref") is { } reference)
        {
            lock (gate)
                if (refs.TryGetValue(reference, out var element)) return element;
            throw new ArgumentException($"Unknown ref {reference}; take a new snapshot.");
        }
        if (args.String("name") is null && args.String("control_type") is null && args.String("automation_id") is null)
            throw new ArgumentException("Specify ref, name, control_type or automation_id.");
        var index = args.Int("index", 0);
        var match = Find(args).Skip(index).FirstOrDefault();
        return match ?? throw new InvalidOperationException("No matching element. Take a snapshot to see available controls.");
    }

    private static string Label(AutomationElement element)
    {
        try { return $"{element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")} \"{Truncate(element.Current.Name, 80)}\""; }
        catch (ElementNotAvailableException) { return "(element no longer available)"; }
    }

    private static Task RunUiAsync(Action action, CancellationToken token) =>
        Task.Run(action, token).WaitAsync(TimeSpan.FromSeconds(15), token);

    private async Task<string> ClickAsync(AutomationElement element, bool mouse, bool doubleClick, bool right, CancellationToken token)
    {
        if (!element.Current.IsEnabled) throw new InvalidOperationException(Label(element) + " is disabled.");
        mouse |= doubleClick || right;
        // Win32 dialog buttons (message boxes, file pickers): the managed UIA proxy needs foreground focus, WM_COMMAND does not.
        if (!mouse && Native.TryClickDialogButton(new IntPtr(element.Current.NativeWindowHandle)))
            return "Clicked dialog button " + Label(element);
        if (!mouse)
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                await RunUiAsync(((InvokePattern)invoke).Invoke, token);
                return "Invoked " + Label(element);
            }
            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            {
                await RunUiAsync(((TogglePattern)toggle).Toggle, token);
                return $"Toggled {Label(element)} -> {((TogglePattern)toggle).Current.ToggleState}";
            }
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                await RunUiAsync(((SelectionItemPattern)select).Select, token);
                return "Selected " + Label(element);
            }
            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            {
                var pattern = (ExpandCollapsePattern)expand;
                await RunUiAsync(pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed ? pattern.Expand : pattern.Collapse, token);
                return "Expanded/collapsed " + Label(element);
            }
        }
        var point = element.TryGetClickablePoint(out var clickable) ? clickable
            : new System.Windows.Point(element.Current.BoundingRectangle.Left + element.Current.BoundingRectangle.Width / 2,
                element.Current.BoundingRectangle.Top + element.Current.BoundingRectangle.Height / 2);
        Native.Focus(MainWindow());
        Native.Click((int)point.X, (int)point.Y, doubleClick ? 2 : 1, right);
        return $"Mouse-{(right ? "right-" : "")}clicked {Label(element)} at {(int)point.X},{(int)point.Y}";
    }

    private async Task<string> SelectOptionAsync(AutomationElement element, string option, CancellationToken token)
    {
        var wanted = Normalize(option);
        var expandable = element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) ? (ExpandCollapsePattern)expand : null;
        if (expandable is not null)
        {
            await RunUiAsync(expandable.Expand, token);
            await Task.Delay(250, token);
        }
        var items = element.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem))).Cast<AutomationElement>().ToList();
        var names = items.Select(item => item.Current.Name).ToList();
        var match = items.FirstOrDefault(item => Normalize(item.Current.Name) == wanted)
            ?? items.FirstOrDefault(item => Normalize(item.Current.Name).Contains(wanted, StringComparison.Ordinal))
            ?? items.FirstOrDefault(item => Normalize(ReadText(item)).Contains(wanted, StringComparison.Ordinal));
        if (match is null)
        {
            if (expandable is not null) await RunUiAsync(expandable.Collapse, token);
            throw new InvalidOperationException($"No option matching '{option}'. Options: {string.Join(" | ", names.Select(name => Truncate(name, 100)))}");
        }
        await RunUiAsync(((SelectionItemPattern)match.GetCurrentPattern(SelectionItemPattern.Pattern)).Select, token);
        if (expandable is not null && expandable.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
            await RunUiAsync(expandable.Collapse, token);
        return $"Selected '{Truncate(match.Current.Name, 120)}' in {Label(element)}";
    }

    private static string ReadText(AutomationElement element)
    {
        var parts = new List<string>();
        var cached = element.GetUpdatedCache(Cache());
        var stack = new Stack<AutomationElement>();
        stack.Push(cached);
        while (stack.Count > 0 && parts.Count < 400)
        {
            var item = stack.Pop();
            var name = Cached(item, AutomationElement.NameProperty) as string;
            var value = Cached(item, ValuePattern.ValueProperty) as string;
            if (!string.IsNullOrWhiteSpace(name) && (parts.Count == 0 || parts[^1] != name)) parts.Add(name);
            if (!string.IsNullOrWhiteSpace(value) && value != name) parts.Add(value);
            var children = item.CachedChildren.Cast<AutomationElement>().ToArray();
            for (var i = children.Length - 1; i >= 0; i--) stack.Push(children[i]);
        }
        return string.Join("\n", parts);
    }

    private static object ReadGrid(AutomationElement grid, int maxRows)
    {
        var headers = new List<string>();
        if (grid.TryGetCurrentPattern(TablePattern.Pattern, out var table))
            headers.AddRange(((TablePattern)table).Current.GetColumnHeaders().Select(header => header.Current.Name));
        var rows = new List<string[]>();
        var total = 0;
        if (grid.TryGetCurrentPattern(GridPattern.Pattern, out var gridPattern))
        {
            var pattern = (GridPattern)gridPattern;
            total = pattern.Current.RowCount;
            for (var row = 0; row < Math.Min(total, maxRows); row++)
            {
                var cells = new string[pattern.Current.ColumnCount];
                for (var column = 0; column < cells.Length; column++)
                {
                    var cell = pattern.GetItem(row, column);
                    cells[column] = cell.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                        ? ((ValuePattern)value).Current.Value ?? "" : ReadText(cell).Replace("\n", " ");
                }
                rows.Add(cells);
            }
        }
        else
        {
            var items = grid.FindAll(TreeScope.Children, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))).Cast<AutomationElement>().ToList();
            total = items.Count;
            rows.AddRange(items.Take(maxRows).Select(item => ReadText(item).Split('\n')));
        }
        return new { grid = Label(grid), totalRows = total, headers, rows };
    }

    private async Task<string> WaitForAsync(string text, bool gone, int timeoutSeconds, CancellationToken token)
    {
        var wanted = Normalize(text);
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 3600));
        var started = DateTime.UtcNow;
        while (true)
        {
            var hit = Walk().Select(item => item.Element).FirstOrDefault(element =>
                Normalize(Cached(element, AutomationElement.NameProperty) as string).Contains(wanted, StringComparison.Ordinal) ||
                Normalize(Cached(element, ValuePattern.ValueProperty) as string).Contains(wanted, StringComparison.Ordinal));
            if ((hit is not null) != gone)
                return gone ? $"'{text}' is gone after {(DateTime.UtcNow - started).TotalSeconds:N1}s"
                    : $"Found {Describe(hit!).Line} after {(DateTime.UtcNow - started).TotalSeconds:N1}s";
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for '{text}'{(gone ? " to disappear" : "")}.");
            await Task.Delay(500, token);
        }
    }

    private string PressKeys(string keys)
    {
        var main = MainWindow();
        var window = Windows().FirstOrDefault(item => (main is null || !Automation.Compare(item, main)) && item.Current.Name.Length > 0) ?? main;
        Native.Focus(window);
        Native.SendChord(keys);
        return $"Sent {keys} to \"{window?.Current.Name}\"";
    }

    private (byte[] Png, string Path) Screenshot(string? title, string? path, int maxWidth)
    {
        var window = (title is null ? MainWindow() : Windows().FirstOrDefault(item => item.Current.Name.Contains(title, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("Window not found.");
        var png = Native.CaptureWindow(new IntPtr(window.Current.NativeWindowHandle), Math.Max(200, maxWidth));
        var destination = Path.GetFullPath(path ?? Path.Combine(Path.GetTempPath(), "AudioTranscriber-mcp-ui", $"screenshot-{DateTime.Now:HHmmss-fff}.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, png);
        return (png, destination);
    }

    private sealed record ElementInfo(string Ref, string Type, string Name, string Line);

    private static class Native
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, string lParam);

        public static bool TryClickDialogButton(IntPtr button)
        {
            if (button == IntPtr.Zero || ClassName(button) != "Button") return false;
            var parent = GetParent(button);
            var id = GetDlgCtrlID(button);
            if (parent == IntPtr.Zero || id == 0) return false;
            return PostMessage(parent, 0x0111, new IntPtr(id & 0xFFFF), button); // WM_COMMAND, BN_CLICKED
        }

        public static bool TrySetEditText(IntPtr edit, string text)
        {
            if (edit == IntPtr.Zero || ClassName(edit) != "Edit") return false;
            SendMessage(edit, 0x000C, IntPtr.Zero, text); // WM_SETTEXT
            return true;
        }

        private static string ClassName(IntPtr handle)
        {
            var name = new StringBuilder(64);
            GetClassName(handle, name, name.Capacity);
            return name.ToString();
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint Size; public int Width, Height; public ushort Planes, BitCount;
            public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }

        public static void EnableDpiAwareness()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } // Per-monitor v2: physical coordinates for UIA/GDI.
            catch (EntryPointNotFoundException) { }
        }

        public static void Focus(AutomationElement? window)
        {
            if (window is null) return;
            var handle = new IntPtr(window.Current.NativeWindowHandle);
            if (IsIconic(handle)) ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            Thread.Sleep(100);
        }

        public static void Click(int x, int y, int clicks, bool right = false)
        {
            SetCursorPos(x, y);
            Thread.Sleep(50);
            for (var i = 0; i < clicks; i++)
            {
                if (right) Send(Mouse(0x0008), Mouse(0x0010));
                else Send(Mouse(0x0002), Mouse(0x0004));
                Thread.Sleep(60);
            }
        }

        public static void SendChord(string chord)
        {
            var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var codes = parts.Select(KeyCode).ToArray();
            var inputs = codes.Select(code => Key(code, false)).Concat(codes.Reverse().Select(code => Key(code, true))).ToArray();
            Send(inputs);
        }

        private static ushort KeyCode(string key) => key.ToLowerInvariant() switch
        {
            "enter" or "return" => 0x0D, "escape" or "esc" => 0x1B, "tab" => 0x09, "space" => 0x20, "backspace" => 0x08,
            "delete" or "del" => 0x2E, "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27, "home" => 0x24, "end" => 0x23,
            "alt" => 0x12, "ctrl" or "control" => 0x11, "shift" => 0x10, "f2" => 0x71, "f4" => 0x73, "f5" => 0x74, "f10" => 0x79,
            "apps" or "menu" => 0x5D,
            var single when single.Length == 1 && char.IsLetterOrDigit(single[0]) => char.ToUpperInvariant(single[0]),
            _ => throw new ArgumentException("Unsupported key: " + key)
        };

        private static INPUT Mouse(uint flags) => new() { Type = 0, Data = new() { Mouse = new() { Flags = flags } } };
        // Navigation keys need the extended flag; otherwise Shift+arrow arrives as a NumLock keypad key without Shift.
        private static INPUT Key(ushort code, bool up) => new()
        {
            Type = 1,
            Data = new() { Keyboard = new() { Key = code, Flags = (up ? 2u : 0u) | (code is >= 0x21 and <= 0x2E or 0x5D ? 1u : 0u) } }
        };
        private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        public static byte[] CaptureWindow(IntPtr handle, int maxWidth)
        {
            GetWindowRect(handle, out var rect);
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("The window has no visible area.");
            var screen = GetDC(IntPtr.Zero);
            var memory = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, width, height);
            var previous = SelectObject(memory, bitmap);
            try
            {
                if (!PrintWindow(handle, memory, 2)) throw new InvalidOperationException("PrintWindow failed.");
                var header = new BITMAPINFOHEADER
                {
                    Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = -height, Planes = 1, BitCount = 32
                };
                var pixels = new byte[width * height * 4];
                SelectObject(memory, previous);
                if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) throw new InvalidOperationException("GetDIBits failed.");
                return Png.Encode(pixels, width, height, maxWidth);
            }
            finally
            {
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    private static class Png
    {
        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        /// <summary>Encodes top-down BGRA pixels as RGB PNG, box-downscaling by an integer factor to fit maxWidth.</summary>
        public static byte[] Encode(byte[] bgra, int width, int height, int maxWidth)
        {
            var factor = Math.Max(1, (int)Math.Ceiling(width / (double)maxWidth));
            int outWidth = width / factor, outHeight = height / factor;
            var raw = new byte[outHeight * (outWidth * 3 + 1)];
            for (var y = 0; y < outHeight; y++)
            {
                var row = y * (outWidth * 3 + 1);
                for (var x = 0; x < outWidth; x++)
                {
                    int b = 0, g = 0, r = 0;
                    for (var dy = 0; dy < factor; dy++)
                        for (var dx = 0; dx < factor; dx++)
                        {
                            var source = ((y * factor + dy) * width + x * factor + dx) * 4;
                            b += bgra[source]; g += bgra[source + 1]; r += bgra[source + 2];
                        }
                    var area = factor * factor;
                    raw[row + 1 + x * 3] = (byte)(r / area);
                    raw[row + 2 + x * 3] = (byte)(g / area);
                    raw[row + 3 + x * 3] = (byte)(b / area);
                }
            }
            using var output = new MemoryStream();
            output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)outWidth);
            WriteBigEndian(header, 4, (uint)outHeight);
            header[8] = 8; header[9] = 2;
            Chunk(output, "IHDR", header);
            using (var compressed = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(raw);
                Chunk(output, "IDAT", compressed.ToArray());
            }
            Chunk(output, "IEND", []);
            return output.ToArray();
        }

        private static void Chunk(Stream output, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            output.Write(length);
            var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(typed);
            var crc = 0xFFFFFFFFu;
            foreach (var value in typed) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            var checksum = new byte[4];
            WriteBigEndian(checksum, 0, crc ^ 0xFFFFFFFFu);
            output.Write(checksum);
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value;
        }
    }

    public static void EnableDpiAwareness() => Native.EnableDpiAwareness();
}
