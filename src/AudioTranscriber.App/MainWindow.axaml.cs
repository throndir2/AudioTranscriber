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
        BuildTranscriptMenu();
    }

    private void BuildTranscriptMenu()
    {
        if (TranscriptGrid.ContextMenu is null) return;
        var menu = TranscriptGrid.ContextMenu;
        menu.Items.Clear();
        var rows = viewModel.SelectedRows;
        if (rows.Count == 0) return;
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
    }

    private void SessionListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SessionList).Properties.IsRightButtonPressed) return;
        if (FindAncestor<ListBoxItem>(e.Source as Avalonia.Visual) is { } item)
        {
            if (!item.IsSelected) SessionList.SelectedItem = item.DataContext;
            item.Focus();
        }
        BuildSessionMenu();
    }

    private void SessionListSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        viewModel.UpdateSessionSelection(SessionList.SelectedItems.OfType<AudioTranscriber.Storage.StoredSession>());

    private void RestoreSessionSelection(IReadOnlyCollection<Guid> ids)
    {
        foreach (var session in viewModel.Sessions)
            if (ids.Contains(session.Id) && !SessionList.SelectedItems.Contains(session)) SessionList.SelectedItems.Add(session);
    }

    private void BuildSessionMenu()
    {
        if (SessionList.ContextMenu is null) return;
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
