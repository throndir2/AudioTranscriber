using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioTranscriber.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool closed, closing;
    public bool SmokeMode { get; init; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height - 24);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        this.viewModel = viewModel;
        DataContext = viewModel;
        Closing += OnClosing;
        StateChanged += (_, _) => ApplyWindowState();
        ApplyWindowState();
        viewModel.TranscriptReloaded += RestoreTranscriptSelection;
        viewModel.ActivityLog.CollectionChanged += (_, e) =>
        {
            // Defer until the ListBox has processed the change; scrolling inside the event corrupts its generator.
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(() =>
                {
                    if (viewModel.ActivityLog.Count > 0) ActivityList.ScrollIntoView(viewModel.ActivityLog[^1]);
                }, System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    // A maximized custom-chrome window overhangs the screen by its resize frame; inset the content to match.
    private void ApplyWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        var pad = WindowTheme.PaddedBorder(this);
        RootGrid.Margin = maximized
            ? new Thickness(frame.Left + pad, frame.Top + pad, frame.Right + pad, frame.Bottom + pad)
            : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore down" : "Maximize");
    }

    private void SaveKeyClick(object sender, RoutedEventArgs e)
    {
        viewModel.SaveKey(NvidiaKeyInput.Password);
        NvidiaKeyInput.Clear();
    }

    private void ClearKeyClick(object sender, RoutedEventArgs e)
    {
        NvidiaKeyInput.Clear();
        viewModel.ClearKeyCommand.Execute(null);
    }

    private void TranscriptDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is DataGridRow && viewModel.PlayRowCommand.CanExecute(null))
            viewModel.PlayRowCommand.Execute(null);
    }

    private void TranscriptSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        viewModel.UpdateSelection(TranscriptGrid.SelectedItems.OfType<TranscriptItem>());

    // Page reloads rebuild the rows; put a multi-line selection back.
    private void RestoreTranscriptSelection(bool scrollToTop)
    {
        var ids = viewModel.SelectedRows.SelectMany(item => item.Rows).Select(row => row.Id).ToHashSet();
        viewModel.RestoringSelection = true;
        try
        {
            foreach (var item in viewModel.Transcript)
                if (item.Rows.Any(row => ids.Contains(row.Id)) && !TranscriptGrid.SelectedItems.Contains(item)) TranscriptGrid.SelectedItems.Add(item);
        }
        finally { viewModel.RestoringSelection = false; }
        viewModel.UpdateSelection(TranscriptGrid.SelectedItems.OfType<TranscriptItem>());
        if (scrollToTop && viewModel.Transcript.Count > 0)
            Dispatcher.BeginInvoke(() =>
            {
                if (viewModel.Transcript.Count > 0) TranscriptGrid.ScrollIntoView(viewModel.Transcript[0]);
            }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Right-clicking a line outside the selection selects just that line, like Explorer.
    private void TranscriptRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is DataGridRow { IsSelected: false } row)
        {
            TranscriptGrid.SelectedItems.Clear();
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void TranscriptContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = TranscriptGrid.ContextMenu;
        menu.Items.Clear();
        var rows = viewModel.SelectedRows;
        if (rows.Count == 0) { e.Handled = true; return; }
        var ids = rows.Select(row => row.Row.SpeakerId).Distinct().ToArray();
        var shared = ids.Length == 1 ? ids[0] : null;
        var count = rows.Count == 1 ? "this line" : $"{rows.Count} lines";

        var set = new MenuItem { Header = $"Set speaker for {count}" };
        foreach (var speaker in viewModel.Speakers.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                     .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var id = speaker.Id;
            set.Items.Add(Item(speaker.Name, () => viewModel.AssignSelection(id, null), isChecked: ids.Length == 1 && shared == id));
        }
        if (set.Items.Count > 0) set.Items.Add(new Separator());
        set.Items.Add(Item("New speaker…", viewModel.AssignSelectionToNewSpeaker));
        set.Items.Add(Item("Unknown / unassigned", () => viewModel.AssignSelection(null, null)));
        menu.Items.Add(set);
        menu.Items.Add(Item("Type a speaker name…", viewModel.AssignSelectionToNewSpeaker));
        if (shared is not null && viewModel.Speakers.FirstOrDefault(x => x.Id == shared) is { } current)
            menu.Items.Add(Item($"Rename \"{current.Name}\" everywhere…", () => _ = viewModel.RenameSpeakerInteractiveAsync(current.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("▶ Play line", viewModel.PlaySelectedRow, enabled: rows.Count == 1));
        menu.Items.Add(Item("Edit text", () =>
        {
            SelectedLineTab.IsSelected = true;
            Dispatcher.BeginInvoke(() => { CorrectionInput.Focus(); CorrectionInput.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        }, enabled: rows.Count == 1));
    }

    private static MenuItem Item(string header, Action action, bool isChecked = false, bool enabled = true)
    {
        // Underscores in speaker names are literal, not access keys.
        var item = new MenuItem { Header = header.Replace("_", "__"), IsChecked = isChecked, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        if (!SmokeMode && (viewModel.IsRecording || viewModel.Busy) &&
            MessageBox.Show(this,
                "Close AudioTranscriber?\n\nAn active recording will be stopped and its original audio tails sealed before exit. The current import/download/analysis may be canceled. No hidden transcription cancellation is used to skip capture finalization.",
                "Finish active work before closing", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        closing = true;
        IsEnabled = false;
        try
        {
            await viewModel.ShutdownAsync();
            NvidiaKeyInput.Clear();
            closed = true;
            Close();
        }
        catch
        {
            IsEnabled = true;
            closing = false;
            MessageBox.Show(this, "Shutdown could not finish safely. The window remains open; review the session state and retained original files before retrying close.",
                "Shutdown needs attention", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
