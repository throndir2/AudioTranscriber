using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioTranscriber.Application;
using AudioTranscriber.Integrations;
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
        validation.Foreground = Brushes.Firebrick;
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
        ShowInTaskbar = false, Background = Brushes.White
    };

    private static TextBlock Help(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        Foreground = new SolidColorBrush(Color.FromRgb(70, 90, 109))
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
