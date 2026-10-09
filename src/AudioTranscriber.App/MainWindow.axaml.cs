using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AudioTranscriber.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool closed, closing, followTranscriptEnd = true;
    public bool SmokeMode { get; init; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Closing += OnClosing;
        // Rows and list items handle right presses themselves, so listen in the tunnel phase to select the
        // row first; the menus are then built when they open (mouse, Menu key or Shift+F10).
        TranscriptGrid.AddHandler(PointerPressedEvent, TranscriptPointerPressed, RoutingStrategies.Tunnel);
        SessionList.AddHandler(PointerPressedEvent, SessionListPointerPressed, RoutingStrategies.Tunnel);
        TranscriptGrid.ContextMenu!.Opening += (_, e) => e.Cancel = !BuildTranscriptMenu();
        SessionList.ContextMenu!.Opening += (_, e) => e.Cancel = !BuildSessionMenu();
        if (OperatingSystem.IsWindows()) UseCustomTitleBar();
        else foreach (var tab in MainTabs.Items.OfType<TabItem>()) tab.Tag = null;
        viewModel.TranscriptReloading += () => followTranscriptEnd = true;
        viewModel.TranscriptReloaded += RestoreTranscriptSelection;
        viewModel.SessionsReloaded += RestoreSessionSelection;
        viewModel.ActivityLog.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                Dispatcher.UIThread.Post(() =>
                {
                    if (viewModel.ActivityLog.Count > 0) ActivityList.ScrollIntoView(viewModel.ActivityLog[^1]);
                }, DispatcherPriority.Background);
        };
    }

    private void SaveKeyClick(object? sender, RoutedEventArgs e)
    {
        viewModel.SaveKey(NvidiaKeyInput.Text ?? "");
        NvidiaKeyInput.Text = "";
    }

    private void ClearKeyClick(object? sender, RoutedEventArgs e)
    {
        NvidiaKeyInput.Text = "";
        viewModel.ClearKeyCommand.Execute(null);
    }

    private void SaveLlmKeyClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(LlmKeyInput.Text)) return;
        viewModel.Templates.SetKey(LlmKeyInput.Text);
        LlmKeyInput.Text = "";
    }

    private void ClearLlmKeyClick(object? sender, RoutedEventArgs e)
    {
        LlmKeyInput.Text = "";
        viewModel.Templates.SetKey("");
    }

    private void TranscriptDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.Source as Avalonia.Visual) is not null && viewModel.PlayRowCommand.CanExecute(null))
            viewModel.PlayRowCommand.Execute(null);
    }

    private void TranscriptSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        viewModel.UpdateSelection(TranscriptGrid.SelectedItems.OfType<TranscriptItem>());

    private void RestoreTranscriptSelection(bool reset, TranscriptItem? target)
    {
        var ids = viewModel.SelectedRows.SelectMany(item => item.Rows).Select(row => row.Id).ToHashSet();
        viewModel.RestoringSelection = true;
        try
        {
            TranscriptGrid.SelectedItems.Clear();
            foreach (var item in viewModel.Transcript)
                if (item.Rows.Any(row => ids.Contains(row.Id))) TranscriptGrid.SelectedItems.Add(item);
        }
        finally { viewModel.RestoringSelection = false; }
        viewModel.UpdateSelection(TranscriptGrid.SelectedItems.OfType<TranscriptItem>());
        Dispatcher.UIThread.Post(() =>
        {
            var item = target ?? (viewModel.Transcript.Count > 0 ? viewModel.Transcript[^1] : null);
            if (item is not null) TranscriptGrid.ScrollIntoView(item, null);
        }, DispatcherPriority.Background);
    }

    private void TranscriptPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TranscriptGrid).Properties.IsRightButtonPressed) return;
        if (FindAncestor<DataGridRow>(e.Source as Avalonia.Visual) is { } row && row.DataContext is TranscriptItem item && !TranscriptGrid.SelectedItems.Contains(item))
        {
            TranscriptGrid.SelectedItems.Clear();
            TranscriptGrid.SelectedItems.Add(item);
            TranscriptGrid.SelectedItem = item;
            row.Focus();
        }
    }

    private bool BuildTranscriptMenu()
    {
        if (TranscriptGrid.ContextMenu is null) return false;
        var menu = TranscriptGrid.ContextMenu;
        menu.Items.Clear();
        var rows = viewModel.SelectedRows;
        if (rows.Count == 0) return false;
        var ids = rows.Select(row => row.Row.SpeakerId).Distinct().ToArray();
        var shared = ids.Length == 1 ? ids[0] : null;
        var count = rows.Count == 1 ? "this line" : $"{rows.Count} lines";
        var set = new MenuItem { Header = $"Set speaker for {count}" };
        foreach (var speaker in viewModel.Speakers.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var id = speaker.Id;
            set.Items.Add(Item(speaker.Name, () => viewModel.AssignSelection(id, null), ids.Length == 1 && shared == id));
        }
        if (set.Items.Count > 0) set.Items.Add(new Separator());
        set.Items.Add(Item("New speaker…", viewModel.AssignSelectionToNewSpeaker));
        set.Items.Add(Item("Unknown / unassigned", () => viewModel.AssignSelection(null, null)));
        menu.Items.Add(set);
        menu.Items.Add(Item("Type a speaker name…", viewModel.AssignSelectionToNewSpeaker));
        menu.Items.Add(Item("Name all microphone lines…", () => Dispatcher.UIThread.Post(() => _ = viewModel.NameMicrophoneLinesAsync()), enabled: viewModel.NameMicrophoneLinesCommand.CanExecute(null)));
        if (shared is not null && viewModel.Speakers.FirstOrDefault(x => x.Id == shared) is { } current)
            menu.Items.Add(Item($"Rename \"{current.Name}\" everywhere…", () => _ = viewModel.RenameSpeakerInteractiveAsync(current.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("▶ Play line", viewModel.PlaySelectedRow, enabled: rows.Count == 1));
        menu.Items.Add(Item("Edit text", () =>
        {
            SelectedLineTab.IsSelected = true;
            Dispatcher.UIThread.Post(() => { CorrectionInput.Focus(); CorrectionInput.SelectAll(); }, DispatcherPriority.Input);
        }, enabled: rows.Count == 1));
        return true;
    }

    private void SessionListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SessionList).Properties.IsRightButtonPressed) return;
        if (FindAncestor<ListBoxItem>(e.Source as Avalonia.Visual) is { } item)
        {
            if (!item.IsSelected) SessionList.SelectedItem = item.DataContext;
            item.Focus();
        }
    }

    private void SessionListSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        viewModel.UpdateSessionSelection(SessionList.SelectedItems.OfType<AudioTranscriber.Storage.StoredSession>());

    private void RestoreSessionSelection(IReadOnlyCollection<Guid> ids)
    {
        foreach (var session in viewModel.Sessions)
            if (ids.Contains(session.Id) && !SessionList.SelectedItems.Contains(session)) SessionList.SelectedItems.Add(session);
    }

    private bool BuildSessionMenu()
    {
        if (SessionList.ContextMenu is null) return false;
        var menu = SessionList.ContextMenu;
        menu.Items.Clear();
        Action Later(Action action) => () => Dispatcher.UIThread.Post(action, DispatcherPriority.Input);
        var selected = viewModel.SelectedSessions;
        if (selected.Count > 1)
        {
            menu.Items.Add(Item($"Merge {selected.Count:N0} selected sessions…", Later(() => _ = viewModel.MergeSelectedSessionsAsync()), enabled: viewModel.CanMergeSelectedSessions));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Delete {selected.Count:N0} selected sessions…", Later(() => _ = viewModel.DeleteSelectedSessionsAsync()), enabled: viewModel.CanDeleteSelectedSessions));
            menu.Items.Add(new Separator());
        }
        else if (viewModel.SelectedSession is { } session)
        {
            var name = session.Name.Length > 40 ? session.Name[..39] + "…" : session.Name;
            menu.Items.Add(Item($"● Continue recording into \"{name}\"", Later(() => viewModel.ContinueRecordingCommand.Execute(null)), enabled: viewModel.ContinueRecordingCommand.CanExecute(null)));
            menu.Items.Add(Item("Merge with other sessions…", Later(() => viewModel.MergeSessionsCommand.Execute(null)), enabled: viewModel.MergeSessionsCommand.CanExecute(null)));
            menu.Items.Add(Item("Name all microphone lines…", Later(() => _ = viewModel.NameMicrophoneLinesAsync()), enabled: viewModel.NameMicrophoneLinesCommand.CanExecute(null)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Delete \"{name}\"…", Later(() => _ = viewModel.DeleteSessionAsync(session)), enabled: viewModel.CanDeleteSession(session)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Delete old sessions…", Later(() => viewModel.DeleteSessionsCommand.Execute(null)), enabled: viewModel.DeleteSessionsCommand.CanExecute(null)));
        return true;
    }

    private void SessionListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        if (viewModel.SelectedSessions.Count > 1) _ = viewModel.DeleteSelectedSessionsAsync();
        else if (viewModel.DeleteSessionCommand.CanExecute(null)) viewModel.DeleteSessionCommand.Execute(null);
        else if (viewModel.SelectedSession is { } session && viewModel.IsRecordingSession(session.Id)) _ = viewModel.DeleteSessionAsync(session);
    }

    private static MenuItem Item(string header, Action action, bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header.Replace("_", "__"), IsChecked = isChecked, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void UseCustomTitleBar()
    {
        Classes.Add("windows");
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = 40;
        // NoChrome greys out SC_CLOSE, which also blocks Alt+F4 and the taskbar's "Close window"; route them back to Close().
        Win32Properties.AddWndProcHookCallback(this, (IntPtr _, uint msg, IntPtr wParam, IntPtr _, ref bool handled) =>
        {
            const uint WmSysCommand = 0x0112, WmSysKeyDown = 0x0104;
            if ((msg == WmSysCommand && ((int)wParam & 0xFFF0) == 0xF060) || (msg == WmSysKeyDown && (int)wParam == 0x73))
            {
                handled = true;
                Dispatcher.UIThread.Post(Close);
            }
            return IntPtr.Zero;
        });
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty && e.Property != OffScreenMarginProperty) return;
            Padding = OffScreenMargin;
            UpdateMaximizeGlyph();
        };
    }

    private void UpdateMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        var label = maximized ? "Restore" : "Maximize";
        ToolTip.SetTip(MaximizeButton, label);
        AutomationProperties.SetName(MaximizeButton, label);
    }

    private void MinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        if (!SmokeMode && (viewModel.IsRecording || viewModel.Busy) &&
            !await DesktopDialogs.ShowConfirmAsync(this, "Finish active work before closing",
                "Close AudioTranscriber?\n\nAn active recording will be stopped and sealed before exit. The current import/download/analysis may be canceled.")) return;
        closing = true;
        IsEnabled = false;
        try
        {
            await viewModel.ShutdownAsync();
            NvidiaKeyInput.Text = "";
            closed = true;
            Close();
        }
        catch
        {
            IsEnabled = true;
            closing = false;
            await DesktopDialogs.ShowMessageAsync(this, "Shutdown needs attention", "Shutdown could not finish safely. The window remains open; review the session state and retained original files before retrying close.", NativeDialogIcon.Error);
        }
    }

    private static T? FindAncestor<T>(Avalonia.Visual? source) where T : Avalonia.Visual
    {
        while (source is not null)
        {
            if (source is T target) return target;
            source = source.GetVisualParent();
        }
        return null;
    }
}
