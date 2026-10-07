using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AudioTranscriber.Application;
using AudioTranscriber.Integrations;
using AudioTranscriber.Storage;

namespace AudioTranscriber.App;

public enum NativeDialogIcon { Info, Warning, Error, Question }

public sealed class DesktopDialogs(Func<Window> owner)
{
    private Window? signInWindow;

    public Task<string?> OpenAudioAsync() => PickFileAsync("Choose an audio or audio-bearing video file",
        ["*.wav", "*.flac", "*.mp3", "*.m4a", "*.aac", "*.ogg", "*.opus", "*.wma", "*.mp4", "*.mkv", "*.mov", "*.webm", "*.avi"]);

    public Task<string?> OpenVttAsync() => PickFileAsync("Import WebVTT into the selected session", ["*.vtt"]);
    public Task<string?> OpenWhisperModelAsync() => PickFileAsync("Choose an existing compatible whisper.cpp model (no download)", ["*.bin"]);

    public Task<string?> SaveExportAsync(string sessionName) => SaveFileAsync("Export the complete selected-session transcript",
        Safe(sessionName, "transcript"), ["*.txt", "*.json", "*.srt", "*.vtt"]);

    public Task<string?> SaveLiveTranscriptAsync(string currentPath) => SaveFileAsync("Choose the live transcript file (kept unlocked so other apps can read it)",
        string.IsNullOrWhiteSpace(currentPath) ? "live-transcript.txt" : Path.GetFileName(currentPath), ["*.txt", "*.md", "*.*"], StartFolder(currentPath));

    public Task<string?> SaveTemplateOutputAsync(string currentPath, string templateName) => SaveFileAsync("Choose the file this template keeps updated (kept unlocked so other apps can read it)",
        string.IsNullOrWhiteSpace(currentPath) ? Safe(templateName, "template") + ".md" : Path.GetFileName(currentPath), ["*.md", "*.txt", "*.*"], StartFolder(currentPath));

    public async Task<string?> ChooseFolderAsync(string currentFolder)
    {
        var result = await owner().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder of reference files (PDFs, notes…) templates may read",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageFolderAsync(currentFolder)
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> ChooseReferenceFilesAsync(string folder)
    {
        var result = await owner().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose files to always include with every template",
            AllowMultiple = true,
            SuggestedStartLocation = await StorageFolderAsync(folder),
            FileTypeFilter = [new FilePickerFileType("Reference files") { Patterns = ["*.pdf", "*.docx", "*.txt", "*.md", "*.markdown", "*.json", "*.csv", "*.yaml", "*.yml", "*.html", "*.htm"] }, FilePickerFileTypes.All]
        });
        return result.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Select(path => path!).ToArray();
    }

    public Task<bool> ConfirmAsync(string title, string message) => ShowConfirmAsync(ownerOrNull(), title, message, NativeDialogIcon.Question);

    public async Task<IReadOnlyList<Guid>?> ChooseSessionsToDeleteAsync(IReadOnlyList<StoredSession> sessions, Guid? recordingSessionId)
    {
        var window = Dialog("Delete sessions", 680);
        var rows = sessions.Select(session => new SessionDeletionRow(session, session.Id != recordingSessionId)).ToArray();
        var panel = new DockPanel { Margin = new Thickness(22) };

        var intro = Help("Check the sessions to delete. Each one's transcript, speakers, jobs and retained original audio are removed from this PC; " +
            "this cannot be undone. Exported transcripts and the live transcript file are not touched." +
            (recordingSessionId is null ? "" : " The session being recorded can't be deleted until you stop it."));
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);

        var quick = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(quick, Dock.Top);
        var ages = new (string Label, int Days)[] { ("1 week", 7), ("30 days", 30), ("90 days", 90), ("6 months", 182), ("1 year", 365) };
        var age = new ComboBox { ItemsSource = ages.Select(x => x.Label).ToArray(), SelectedIndex = 1, Width = 120, Margin = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(age, "Age for quick selection");
        var checkOlder = new Button { Content = "Check older" };
        var checkAll = new Button { Content = "Check all", Margin = new Thickness(8, 0, 0, 0) };
        var checkNone = new Button { Content = "Uncheck all", Margin = new Thickness(8, 0, 0, 0) };
        quick.Children.Add(new TextBlock { Text = "Sessions older than", VerticalAlignment = VerticalAlignment.Center });
        quick.Children.Add(age); quick.Children.Add(checkOlder); quick.Children.Add(checkAll); quick.Children.Add(checkNone);
        panel.Children.Add(quick);

        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var delete = new Button { Content = "Delete checked…", Classes = { "stop" }, Margin = new Thickness(8, 0, 0, 0) };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel); buttons.Children.Add(delete);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons); footer.Children.Add(summary);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);

        var list = new ListBox
        {
            ItemsSource = rows,
            Height = 380,
            ItemTemplate = new FuncDataTemplate<SessionDeletionRow>((row, _) => CheckedSessionRow(row, includeSize: true))
        };
        AutomationProperties.SetName(list, "Sessions to delete");
        panel.Children.Add(list);
        window.Content = panel;

        void UpdateSummary()
        {
            var chosen = rows.Where(row => row.IsChecked).ToArray();
            var bytes = chosen.Sum(row => row.Bytes ?? 0);
            var pending = chosen.Any(row => row.Bytes is null);
            summary.Text = chosen.Length == 0 ? $"{rows.Length:N0} sessions · none checked"
                : $"{chosen.Length:N0} of {rows.Length:N0} checked · {SessionDeletionRow.FormatBytes(bytes)}{(pending ? "+ (still measuring)" : "")} to free";
            delete.IsEnabled = chosen.Length > 0;
        }
        foreach (var row in rows) row.PropertyChanged += (_, _) => UpdateSummary();
        checkOlder.Click += (_, _) =>
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-ages[Math.Max(age.SelectedIndex, 0)].Days);
            foreach (var row in rows) if (row.CanDelete && row.Session.CreatedUtc < cutoff) row.IsChecked = true;
        };
        checkAll.Click += (_, _) => { foreach (var row in rows) if (row.CanDelete) row.IsChecked = true; };
        checkNone.Click += (_, _) => { foreach (var row in rows) row.IsChecked = false; };
        delete.Click += async (_, _) =>
        {
            var chosen = rows.Where(row => row.IsChecked).ToArray();
            if (chosen.Length == 0) return;
            var bytes = chosen.Sum(row => row.Bytes ?? 0);
            var what = chosen.Length == 1 ? $"\"{chosen[0].Name}\"" : $"{chosen.Length:N0} sessions";
            if (await ShowConfirmAsync(window, $"Delete sessions?", $"Permanently delete {what} ({SessionDeletionRow.FormatBytes(bytes)} on disk)?\n\nThis cannot be undone.", NativeDialogIcon.Warning))
                window.Close(true);
        };
        cancel.Click += (_, _) => window.Close(false);
        UpdateSummary();

        using var measuring = new CancellationTokenSource();
        var token = measuring.Token;
        _ = Task.Run(() =>
        {
            foreach (var row in rows)
            {
                if (token.IsCancellationRequested) return;
                var bytes = SessionDeletionRow.Measure(row.Session.Directory);
                Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) row.Bytes = bytes; });
            }
        }, token);
        var accepted = await window.ShowDialog<bool>(owner());
        measuring.Cancel();
        return accepted ? rows.Where(row => row.IsChecked).Select(row => row.Session.Id).ToArray() : null;
    }

    public async Task<IReadOnlyList<Guid>?> ChooseSessionsToMergeAsync(IReadOnlyList<StoredSession> sessions, IReadOnlyCollection<Guid> selectedIds, Guid? recordingSessionId)
    {
        var window = Dialog("Merge sessions", 640);
        static string? Blocked(StoredSession session, Guid? recording) =>
            session.Id == recording ? "recording now" :
            session.State == "Recoverable" ? "needs recovery first (Jobs → Resume)" :
            session.State is "Recorded" or "Created" or "Faulted" ? null : session.State.ToLowerInvariant();
        var rows = sessions.OrderBy(session => session.CreatedUtc)
            .Select(session => new SessionDeletionRow(session, Blocked(session, recordingSessionId) is null, Blocked(session, recordingSessionId) ?? ""))
            .ToArray();
        foreach (var row in rows) row.IsChecked = row.CanDelete && selectedIds.Contains(row.Session.Id);
        var panel = new DockPanel { Margin = new Thickness(22) };
        var intro = Help("Check the sessions that belong together. They are joined in the order they were recorded: the earliest keeps its name, and later sessions' audio, transcript and speakers follow it on one timeline. The later sessions stop being separate entries. This can't be undone.");
        DockPanel.SetDock(intro, Dock.Top); panel.Children.Add(intro);
        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var merge = new Button { Content = "Merge checked…", Classes = { "primary" }, Margin = new Thickness(8, 0, 0, 0) };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel); buttons.Children.Add(merge);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons); footer.Children.Add(summary);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var list = new ListBox { ItemsSource = rows, Height = 380, ItemTemplate = new FuncDataTemplate<SessionDeletionRow>((row, _) => CheckedSessionRow(row, includeSize: false)) };
        AutomationProperties.SetName(list, "Sessions to merge");
        panel.Children.Add(list);
        window.Content = panel;
        SessionDeletionRow[] Chosen() => rows.Where(row => row.IsChecked).ToArray();
        void UpdateSummary()
        {
            var chosen = Chosen();
            summary.Text = chosen.Length < 2 ? "Check at least two sessions." : $"{chosen.Length:N0} sessions → \"{chosen[0].Name}\" · {TranscriptPresentation.Duration(chosen.Sum(row => row.Session.DurationTicks))} in total";
            merge.IsEnabled = chosen.Length > 1;
        }
        foreach (var row in rows) row.PropertyChanged += (_, _) => UpdateSummary();
        merge.Click += async (_, _) =>
        {
            var chosen = Chosen();
            if (chosen.Length < 2) return;
            var later = string.Join("\n", chosen.Skip(1).Select(row => $"  • {row.Name} ({row.Session.CreatedUtc.ToLocalTime():MMM d · HH:mm})"));
            if (await ShowConfirmAsync(window, "Merge sessions?", $"Merge into \"{chosen[0].Name}\" ({chosen[0].Session.CreatedUtc.ToLocalTime():MMM d · HH:mm})?\n\nThese follow it, in this order:\n{later}\n\nThey stop being separate sessions. This cannot be undone.", NativeDialogIcon.Question))
                window.Close(true);
        };
        cancel.Click += (_, _) => window.Close(false);
        UpdateSummary();
        return await window.ShowDialog<bool>(owner()) ? Chosen().Select(row => row.Session.Id).ToArray() : null;
    }

    public async Task<string?> PromptSpeakerNameAsync(string title, string message, string initial, IEnumerable<string> suggestions, string affirmative)
    {
        var window = Dialog(title, 460);
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 4 };
        panel.Children.Add(Help(message));
        var input = new ComboBox { IsEditable = true, ItemsSource = suggestions.ToArray(), Text = initial, IsTextSearchEnabled = false };
        EditableComboBox.SetSyncSelection(input, true);
        AutomationProperties.SetName(input, "Speaker name");
        panel.Children.Add(input);
        panel.Children.Add(Buttons(window, affirmative, () => !string.IsNullOrWhiteSpace(input.Text)));
        window.Content = panel;
        window.Opened += (_, _) => { input.Focus(); };
        var accepted = await window.ShowDialog<bool>(owner());
        return accepted ? input.Text?.Trim() : null;
    }

    public async Task<MediaStreamChoice?> ChooseStreamAsync(MediaProbeSummary probe)
    {
        var window = Dialog("Choose the audio stream", 620);
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 4 };
        panel.Children.Add(Help($"The media contains {probe.Streams.Count} audio streams. Select the one to preserve and transcribe.\nDuration reported by probe: {TimeSpan.FromSeconds(Math.Max(0, probe.DurationSeconds)):g}."));
        var list = new ListBox { ItemsSource = probe.Streams, MinHeight = 120, MaxHeight = 300, DisplayMemberBinding = new Binding(nameof(MediaStreamChoice.Description)) };
        AutomationProperties.SetName(list, "Audio streams reported by FFprobe");
        panel.Children.Add(list);
        panel.Children.Add(Buttons(window, "Import selected stream", () => list.SelectedItem is not null));
        window.Content = panel;
        return await window.ShowDialog<bool>(owner()) ? list.SelectedItem as MediaStreamChoice : null;
    }

    public async Task<TeamsTranscriptRequest?> RequestTeamsTranscriptAsync()
    {
        var window = Dialog("Microsoft Teams transcript · configuration required", 680);
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 4 };
        panel.Children.Add(Help("Real delegated Microsoft Graph retrieval, not live Teams audio. Use a work/school tenant and a public-client app configured for device-code sign-in with OnlineMeetingTranscript.Read.All permission. You must already have meeting access; tenant restrictions are not bypassed."));
        var tenant = Field(panel, "Tenant ID (GUID)");
        var client = Field(panel, "Public client application ID (GUID)");
        var user = Field(panel, "User ID or work/school user principal name");
        var meeting = Field(panel, "Online meeting ID (not a join URL or meeting code)");
        var transcript = Field(panel, "Transcript ID");
        var unattributed = new CheckBox { Content = new TextBlock { Text = "Request the documented unattributed content type only if tenant policy permits it (never reconstruct restricted identities).", TextWrapping = TextWrapping.Wrap } };
        panel.Children.Add(unattributed);
        var validation = Help(""); validation.Foreground = Brush("ErrorText"); panel.Children.Add(validation);
        panel.Children.Add(Buttons(window, "Sign in and fetch", () =>
        {
            if (!Guid.TryParse(tenant.Text, out _) || !Guid.TryParse(client.Text, out _) || new[] { user.Text, meeting.Text, transcript.Text }.Any(string.IsNullOrWhiteSpace))
            {
                validation.Text = "Enter valid tenant and client GUIDs and all three resource identifiers.";
                return false;
            }
            return true;
        }));
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
        return await window.ShowDialog<bool>(owner())
            ? new TeamsTranscriptRequest(tenant.Text!.Trim(), client.Text!.Trim(), user.Text!.Trim(), meeting.Text!.Trim(), transcript.Text!.Trim(), unattributed.IsChecked == true) : null;
    }

    public Task ShowDeviceSignInAsync(DeviceSignInPrompt prompt, Action cancel)
    {
        CloseDeviceSignIn();
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Dialog("Microsoft device-code sign-in", 620);
            signInWindow = window;
            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 8 };
            panel.Children.Add(Help("Sign in with your permitted work/school account. This is a device code, not a token. Authentication and Graph tokens stay inside the Microsoft client."));
            panel.Children.Add(Help("Open this verification URL:"));
            panel.Children.Add(new TextBox { Text = prompt.VerificationUrl, IsReadOnly = true });
            var open = new Button { Content = "Open Microsoft verification page", HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(prompt.VerificationUrl) { UseShellExecute = true }); } catch { _ = ShowMessageAsync(window, "Open verification page", "The browser could not be opened. Copy the verification URL into your browser.", NativeDialogIcon.Warning); } };
            panel.Children.Add(open);
            panel.Children.Add(Help("Enter the actual code supplied by Microsoft:"));
            var code = new TextBox { Text = prompt.UserCode, IsReadOnly = true, FontSize = 28, FontWeight = FontWeight.SemiBold };
            AutomationProperties.SetName(code, "Microsoft device sign-in code");
            panel.Children.Add(code);
            panel.Children.Add(Help($"Expires: {prompt.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\nThe operation waits for sign-in. Keep this window open until completion."));
            var cancelButton = new Button { Content = "Cancel sign-in / fetch", HorizontalAlignment = HorizontalAlignment.Left };
            cancelButton.Click += (_, _) => { cancel(); CloseDeviceSignIn(); };
            panel.Children.Add(cancelButton);
            window.Content = panel;
            window.Closed += (_, _) => { if (ReferenceEquals(signInWindow, window)) { signInWindow = null; cancel(); } };
            window.Show(owner());
        }).GetTask();
    }

    public void CloseDeviceSignIn()
    {
        var window = signInWindow;
        signInWindow = null;
        window?.Close();
    }

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns)
    {
        var result = await owner().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }, FilePickerFileTypes.All]
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> SaveFileAsync(string title, string name, IReadOnlyList<string> patterns, string? startFolder = null)
    {
        var result = await owner().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = name,
            SuggestedStartLocation = await StorageFolderAsync(startFolder),
            FileTypeChoices = [new FilePickerFileType(title) { Patterns = patterns }, FilePickerFileTypes.All]
        });
        return result?.TryGetLocalPath();
    }

    private async Task<IStorageFolder?> StorageFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        return await owner().StorageProvider.TryGetFolderFromPathAsync(folder);
    }

    private static string? StartFolder(string path) => Path.GetDirectoryName(path) is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;

    private static Control CheckedSessionRow(SessionDeletionRow? row, bool includeSize)
    {
        var check = new CheckBox { IsEnabled = row?.CanDelete == true, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch };
        check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(SessionDeletionRow.IsChecked)) { Mode = BindingMode.TwoWay });
        var dock = new DockPanel();
        if (includeSize)
        {
            var size = new TextBlock { Margin = new Thickness(12, 0, 0, 0), FontSize = 12, Foreground = Brush("Muted") };
            size.Bind(TextBlock.TextProperty, new Binding(nameof(SessionDeletionRow.Size)));
            DockPanel.SetDock(size, Dock.Right); dock.Children.Add(size);
        }
        var stack = new StackPanel();
        var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(SessionDeletionRow.Name)));
        var details = new TextBlock { FontSize = 12, Foreground = Brush("Dim"), Margin = new Thickness(0, 2, 0, 0) };
        details.Bind(TextBlock.TextProperty, new Binding(nameof(SessionDeletionRow.Details)));
        stack.Children.Add(name); stack.Children.Add(details); dock.Children.Add(stack); check.Content = dock;
        return check;
    }

    private Window Dialog(string title, double width) => new()
    {
        Title = title,
        Icon = AppIcon(),
        Width = width,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = false,
        ShowInTaskbar = false,
        Background = Brush("WindowFill")
    };

    private static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri("avares://AudioTranscriber.App/Assets/AppIcon.ico")));

    private static TextBlock Help(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 12),
        Foreground = Brush("Muted")
    };

    private static TextBox Field(Panel panel, string label)
    {
        var input = new TextBox();
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brush("Muted"), FontSize = 12.5 });
        AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
        return input;
    }

    private static StackPanel Buttons(Window window, string affirmative, Func<bool> validate)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var accept = new Button { Content = affirmative, IsDefault = true };
        cancel.Click += (_, _) => window.Close(false);
        accept.Click += (_, _) => { if (validate()) window.Close(true); };
        panel.Children.Add(cancel); panel.Children.Add(accept);
        return panel;
    }

    private static string Safe(string value, string fallback)
    {
        var safe = string.Join("_", value.Split(Path.GetInvalidFileNameChars())).Trim();
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    private Window? ownerOrNull()
    {
        try { return owner(); }
        catch { return null; }
    }

    private static IBrush? Brush(string key) => Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var value) == true ? value as IBrush : null;

    public static async Task SetClipboardTextAsync(string text)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            await (TopLevel.GetTopLevel(window)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask);
    }

    public static void CloseMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            window.Close();
    }

    public static Task ShowMessageAsync(string title, string message, NativeDialogIcon icon = NativeDialogIcon.Info) => ShowMessageAsync(null, title, message, icon);
    public static Task ShowMessageAsync(Window? owner, string title, string message, NativeDialogIcon icon = NativeDialogIcon.Info) => ShowDialogCoreAsync(owner, title, message, icon, confirm: false);
    public static Task<bool> ShowConfirmAsync(Window? owner, string title, string message, NativeDialogIcon icon = NativeDialogIcon.Question) => ShowDialogCoreAsync(owner, title, message, icon, confirm: true);

    private static async Task<bool> ShowDialogCoreAsync(Window? owner, string title, string message, NativeDialogIcon icon, bool confirm)
    {
        var window = new Window
        {
            Title = title,
            Icon = AppIcon(),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = owner is null,
            Background = Brush("WindowFill")
        };
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var glyph = new TextBlock { Text = IconGlyph(icon), FontSize = 26, FontWeight = FontWeight.Bold, Foreground = IconBrush(icon), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0) };
        row.Children.Add(glyph);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("Ink"), MaxWidth = 380 };
        Grid.SetColumn(text, 1); row.Children.Add(text); root.Children.Add(row);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (confirm)
        {
            var yes = new Button { Content = "Yes" };
            var no = new Button { Content = "No", IsDefault = true, IsCancel = true };
            yes.Click += (_, _) => window.Close(true);
            no.Click += (_, _) => window.Close(false);
            buttons.Children.Add(yes); buttons.Children.Add(no);
        }
        else
        {
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80 };
            ok.Click += (_, _) => window.Close(true);
            buttons.Children.Add(ok);
        }
        root.Children.Add(buttons);
        window.Content = root;
        if (owner is not null) return await window.ShowDialog<bool>(owner);
        var tcs = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => tcs.TrySetResult(false);
        window.Show();
        return await tcs.Task;
    }

    private static string IconGlyph(NativeDialogIcon icon) => icon switch
    {
        NativeDialogIcon.Error => "X",
        NativeDialogIcon.Warning => "!",
        NativeDialogIcon.Question => "?",
        _ => "i"
    };

    private static IBrush? IconBrush(NativeDialogIcon icon) => icon switch
    {
        NativeDialogIcon.Error => Brush("ErrorText"),
        NativeDialogIcon.Warning => Brush("GoldBright"),
        NativeDialogIcon.Question => Brush("GoldBright"),
        _ => Brush("Accent")
    };
}

public sealed class SessionDeletionRow(StoredSession session, bool canDelete, string blockedNote = "recording now") : ObservableObject
{
    private bool isChecked;
    private long? bytes;
    public StoredSession Session { get; } = session;
    public bool CanDelete { get; } = canDelete;
    public string Name => Session.Name;
    public string Details => $"{Session.CreatedUtc.ToLocalTime():MMM d, yyyy · HH:mm} · {TranscriptPresentation.Duration(Session.DurationTicks)} · {Session.State}" +
        (CanDelete ? "" : " · " + blockedNote);
    public bool IsChecked { get => isChecked; set => Set(ref isChecked, value && CanDelete); }
    public long? Bytes { get => bytes; set { if (Set(ref bytes, value)) Changed(nameof(Size)); } }
    public string Size => Bytes is { } value ? FormatBytes(value) : "…";
    public override string ToString() => Name;
    public static long Measure(string directory)
    {
        try { return Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length) : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }
    public static string FormatBytes(long value) => value switch
    {
        >= 1L << 30 => $"{value / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{value / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{value / 1024d:0} KB",
        _ => $"{value} bytes"
    };
}
