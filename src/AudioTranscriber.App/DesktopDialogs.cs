using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioTranscriber.Application;
using AudioTranscriber.Integrations;
using AudioTranscriber.Storage;
using Microsoft.Win32;

namespace AudioTranscriber.App;

public sealed class DesktopDialogs(Func<Window> owner)
{
    private Window? signInWindow;

    public string? OpenAudio()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an audio or audio-bearing video file",
            Filter = "Audio and video|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.mp4;*.mkv;*.mov;*.webm;*.avi|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? OpenVtt()
    {
        var dialog = new OpenFileDialog { Title = "Import WebVTT into the selected session", Filter = "WebVTT|*.vtt", CheckFileExists = true };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? OpenWhisperModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an existing compatible whisper.cpp model (no download)",
            Filter = "Whisper model|*.bin|All files|*.*",
            CheckFileExists = true
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? SaveExport(string sessionName)
    {
        var safeName = string.Join("_", sessionName.Split(Path.GetInvalidFileNameChars())).Trim();
        var dialog = new SaveFileDialog
        {
            Title = "Export the complete selected-session transcript",
            FileName = string.IsNullOrWhiteSpace(safeName) ? "transcript" : safeName,
            Filter = "Plain text|*.txt|JSON with raw text and provenance|*.json|SubRip subtitles|*.srt|WebVTT subtitles|*.vtt",
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? SaveLiveTranscript(string currentPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose the live transcript file (kept unlocked so other apps can read it)",
            FileName = string.IsNullOrWhiteSpace(currentPath) ? "live-transcript.txt" : Path.GetFileName(currentPath),
            InitialDirectory = Path.GetDirectoryName(currentPath) is { Length: > 0 } folder && Directory.Exists(folder) ? folder : "",
            Filter = "Plain text|*.txt|Markdown|*.md|All files|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = false
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(owner(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Lets the user check sessions to delete, with quick picks by age. Returns null when canceled.</summary>
    public IReadOnlyList<Guid>? ChooseSessionsToDelete(IReadOnlyList<StoredSession> sessions, Guid? recordingSessionId)
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
        System.Windows.Automation.AutomationProperties.SetName(age, "Age for quick selection");
        var checkOlder = new Button { Content = "Check older", ToolTip = "Check every session created before this age (leaves other checks as they are)" };
        var checkAll = new Button { Content = "Check all", Margin = new Thickness(8, 0, 0, 0) };
        var checkNone = new Button { Content = "Uncheck all", Margin = new Thickness(8, 0, 0, 0) };
        quick.Children.Add(new TextBlock { Text = "Sessions older than", VerticalAlignment = VerticalAlignment.Center });
        quick.Children.Add(age);
        quick.Children.Add(checkOlder);
        quick.Children.Add(checkAll);
        quick.Children.Add(checkNone);
        panel.Children.Add(quick);

        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var delete = new Button { Content = "Delete checked…", Style = (Style)System.Windows.Application.Current.FindResource("StopButton"), Margin = new Thickness(8, 0, 0, 0) };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(delete);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(summary);
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);

        var list = new ListBox
        {
            ItemsSource = rows, Height = 380, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <CheckBox IsChecked="{Binding IsChecked}" IsEnabled="{Binding CanDelete}" Margin="0" HorizontalAlignment="Stretch">
                        <DockPanel>
                            <TextBlock DockPanel.Dock="Right" Text="{Binding Size}" Margin="12,0,0,0" FontSize="12" Foreground="{DynamicResource Muted}" />
                            <StackPanel>
                                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                                <TextBlock Text="{Binding Details}" FontSize="12" Foreground="{DynamicResource Dim}" Margin="0,2,0,0" />
                            </StackPanel>
                        </DockPanel>
                    </CheckBox>
                </DataTemplate>
                """)
        };
        System.Windows.Automation.AutomationProperties.SetName(list, "Sessions to delete");
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
        delete.Click += (_, _) =>
        {
            var chosen = rows.Where(row => row.IsChecked).ToArray();
            if (chosen.Length == 0) return;
            var bytes = chosen.Sum(row => row.Bytes ?? 0);
            var what = chosen.Length == 1 ? $"\"{chosen[0].Name}\"" : $"{chosen.Length:N0} sessions";
            if (MessageBox.Show(window, $"Permanently delete {what} ({SessionDeletionRow.FormatBytes(bytes)} on disk)?\n\nThis cannot be undone.",
                    "Delete sessions?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                window.DialogResult = true;
        };
        UpdateSummary();

        // Folder sizes can take a while for long recordings; measure in the background and fill them in.
        var measuring = new CancellationTokenSource();
        var token = measuring.Token;
        var dispatcher = window.Dispatcher;
        _ = Task.Run(() =>
        {
            foreach (var row in rows)
            {
                if (token.IsCancellationRequested) return;
                var bytes = SessionDeletionRow.Measure(row.Session.Directory);
                dispatcher.BeginInvoke(() => { if (!token.IsCancellationRequested) row.Bytes = bytes; });
            }
        }, token);
        var accepted = window.ShowDialog() == true;
        measuring.Cancel();
        return accepted ? rows.Where(row => row.IsChecked).Select(row => row.Session.Id).ToArray() : null;
    }

    /// <summary>Lets the user check two or more sessions to stitch into the earliest one. Returns null when canceled.</summary>
    public IReadOnlyList<Guid>? ChooseSessionsToMerge(IReadOnlyList<StoredSession> sessions, Guid? selectedId, Guid? recordingSessionId)
    {
        var window = Dialog("Merge sessions", 640);
        static string? Blocked(StoredSession session, Guid? recording) =>
            session.Id == recording ? "recording now" :
            session.State == "Recoverable" ? "needs recovery first (Jobs → Resume)" :
            session.State is "Recorded" or "Created" or "Faulted" ? null : session.State.ToLowerInvariant();
        var rows = sessions.OrderBy(session => session.CreatedUtc)
            .Select(session => new SessionDeletionRow(session, Blocked(session, recordingSessionId) is null, Blocked(session, recordingSessionId) ?? ""))
            .ToArray();
        foreach (var row in rows) row.IsChecked = row.Session.Id == selectedId;
        var panel = new DockPanel { Margin = new Thickness(22) };
        var intro = Help("Check the sessions that belong together, for example a meeting that was split when the app restarted. " +
            "They are joined in the order they were recorded: the earliest keeps its name, and each later session's audio, transcript " +
            "and speakers follow it on one timeline. Automatic speaker labels are renumbered; speakers you named alike are combined. " +
            "The later sessions stop being separate entries. This can't be undone.");
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);

        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var merge = new Button { Content = "Merge checked…", Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0) };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(merge);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(summary);
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);

        var list = new ListBox
        {
            ItemsSource = rows, Height = 380, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <CheckBox IsChecked="{Binding IsChecked}" IsEnabled="{Binding CanDelete}" Margin="0" HorizontalAlignment="Stretch">
                        <StackPanel>
                            <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                            <TextBlock Text="{Binding Details}" FontSize="12" Foreground="{DynamicResource Dim}" Margin="0,2,0,0" />
                        </StackPanel>
                    </CheckBox>
                </DataTemplate>
                """)
        };
        System.Windows.Automation.AutomationProperties.SetName(list, "Sessions to merge");
        panel.Children.Add(list);
        window.Content = panel;

        SessionDeletionRow[] Chosen() => rows.Where(row => row.IsChecked).ToArray();
        void UpdateSummary()
        {
            var chosen = Chosen();
            summary.Text = chosen.Length < 2 ? "Check at least two sessions."
                : $"{chosen.Length:N0} sessions → \"{chosen[0].Name}\" · {TranscriptPresentation.Duration(chosen.Sum(row => row.Session.DurationTicks))} in total";
            merge.IsEnabled = chosen.Length > 1;
        }
        foreach (var row in rows) row.PropertyChanged += (_, _) => UpdateSummary();
        merge.Click += (_, _) =>
        {
            var chosen = Chosen();
            if (chosen.Length < 2) return;
            var later = string.Join("\n", chosen.Skip(1).Select(row => $"  • {row.Name} ({row.Session.CreatedUtc.ToLocalTime():MMM d · HH:mm})"));
            if (MessageBox.Show(window, $"Merge into \"{chosen[0].Name}\" ({chosen[0].Session.CreatedUtc.ToLocalTime():MMM d · HH:mm})?\n\n" +
                    $"These follow it, in this order:\n{later}\n\nThey stop being separate sessions. This cannot be undone.",
                    "Merge sessions?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                window.DialogResult = true;
        };
        UpdateSummary();
        return window.ShowDialog() == true ? Chosen().Select(row => row.Session.Id).ToArray() : null;
    }

    /// <summary>Asks for a speaker name; existing names are offered but any text is accepted.</summary>
    public string? PromptSpeakerName(string title, string message, string initial, IEnumerable<string> suggestions, string affirmative)
    {
        var window = Dialog(title, 460);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Help(message));
        var input = new ComboBox { IsEditable = true, ItemsSource = suggestions.ToArray(), Text = initial, IsTextSearchEnabled = false };
        System.Windows.Automation.AutomationProperties.SetName(input, "Speaker name");
        panel.Children.Add(input);
        panel.Children.Add(Buttons(window, affirmative, () => !string.IsNullOrWhiteSpace(input.Text)));
        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            input.Focus();
            (input.Template.FindName("PART_EditableTextBox", input) as TextBox)?.SelectAll();
        };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }

    public MediaStreamChoice? ChooseStream(MediaProbeSummary probe)
    {
        var window = Dialog("Choose the audio stream", 620);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Help($"The media contains {probe.Streams.Count} audio streams. Select the one to preserve and transcribe.\nDuration reported by probe: {TimeSpan.FromSeconds(Math.Max(0, probe.DurationSeconds)):g}."));
        var list = new ListBox { ItemsSource = probe.Streams, DisplayMemberPath = "Description", MinHeight = 120, MaxHeight = 300 };
        System.Windows.Automation.AutomationProperties.SetName(list, "Audio streams reported by FFprobe");
        panel.Children.Add(list);
        var buttons = Buttons(window, "Import selected stream", () => list.SelectedItem is not null);
        panel.Children.Add(buttons);
        window.Content = panel;
        return window.ShowDialog() == true ? list.SelectedItem as MediaStreamChoice : null;
    }

    public TeamsTranscriptRequest? RequestTeamsTranscript()
    {
        var window = Dialog("Microsoft Teams transcript · configuration required", 680);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Help("Real delegated Microsoft Graph retrieval, not live Teams audio. Use a work/school tenant and a public-client app configured for device-code sign-in with OnlineMeetingTranscript.Read.All permission. You must already have meeting access; tenant restrictions are not bypassed."));
        var tenant = Field(panel, "Tenant ID (GUID)");
        var client = Field(panel, "Public client application ID (GUID)");
        var user = Field(panel, "User ID or work/school user principal name");
        var meeting = Field(panel, "Online meeting ID (not a join URL or meeting code)");
        var transcript = Field(panel, "Transcript ID");
        var unattributed = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "Request the documented unattributed content type only if tenant policy permits it (never reconstruct restricted identities).",
                TextWrapping = TextWrapping.Wrap
            }
        };
        panel.Children.Add(unattributed);
        var validation = Help("");
        validation.Foreground = (Brush)System.Windows.Application.Current.FindResource("ErrorText");
        panel.Children.Add(validation);
        panel.Children.Add(Buttons(window, "Sign in and fetch", () =>
        {
            if (!Guid.TryParse(tenant.Text, out _) || !Guid.TryParse(client.Text, out _) ||
                new[] { user.Text, meeting.Text, transcript.Text }.Any(string.IsNullOrWhiteSpace))
            {
                validation.Text = "Enter valid tenant and client GUIDs and all three resource identifiers.";
                return false;
            }
            return true;
        }));
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
        return window.ShowDialog() == true
            ? new TeamsTranscriptRequest(tenant.Text.Trim(), client.Text.Trim(), user.Text.Trim(),
                meeting.Text.Trim(), transcript.Text.Trim(), unattributed.IsChecked == true) : null;
    }

    public Task ShowDeviceSignInAsync(DeviceSignInPrompt prompt, Action cancel) =>
        owner().Dispatcher.InvokeAsync(() =>
        {
            CloseDeviceSignIn();
            var window = Dialog("Microsoft device-code sign-in", 620);
            signInWindow = window;
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Help("Sign in with your permitted work/school account. This is a device code, not a token. Authentication and Graph tokens stay inside the Microsoft client."));
            panel.Children.Add(Help("Open this verification URL:"));
            panel.Children.Add(new TextBox { Text = prompt.VerificationUrl, IsReadOnly = true });
            var open = new Button { Content = "Open Microsoft verification page", HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) =>
            {
                if (Uri.TryCreate(prompt.VerificationUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                    catch { MessageBox.Show(window, "The browser could not be opened. Copy the verification URL into your browser.", "Open verification page"); }
                }
            };
            panel.Children.Add(open);
            panel.Children.Add(Help("Enter the actual code supplied by Microsoft:"));
            var code = new TextBox { Text = prompt.UserCode, IsReadOnly = true, FontSize = 28, FontWeight = FontWeights.SemiBold };
            System.Windows.Automation.AutomationProperties.SetName(code, "Microsoft device sign-in code");
            panel.Children.Add(code);
            panel.Children.Add(Help($"Expires: {prompt.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\nThe operation waits for sign-in. Keep this window open until completion."));
            var cancelButton = new Button { Content = "Cancel sign-in / fetch", HorizontalAlignment = HorizontalAlignment.Left };
            cancelButton.Click += (_, _) => { cancel(); CloseDeviceSignIn(); };
            panel.Children.Add(cancelButton);
            window.Content = panel;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(signInWindow, window)) { signInWindow = null; cancel(); }
            };
            window.Show();
        }).Task;

    public void CloseDeviceSignIn()
    {
        var window = signInWindow;
        signInWindow = null;
        window?.Close();
    }

    private Window Dialog(string title, double width) => new()
    {
        Title = title, Owner = owner(), Width = width, SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false, Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window))
    };

    private static TextBlock Help(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        Foreground = (Brush)System.Windows.Application.Current.FindResource("Muted")
    };

    private static TextBox Field(Panel panel, string label)
    {
        var input = new TextBox();
        panel.Children.Add(new Label { Content = label, Target = input, Padding = new Thickness(0) });
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
        return input;
    }

    private static StackPanel Buttons(Window window, string affirmative, Func<bool> validate)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var accept = new Button { Content = affirmative, IsDefault = true };
        accept.Click += (_, _) => { if (validate()) window.DialogResult = true; };
        panel.Children.Add(cancel);
        panel.Children.Add(accept);
        return panel;
    }
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
    public long? Bytes
    {
        get => bytes;
        set { if (Set(ref bytes, value)) Changed(nameof(Size)); }
    }
    public string Size => Bytes is { } value ? FormatBytes(value) : "…";
    public override string ToString() => Name;

    public static long Measure(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length) : 0;
        }
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
