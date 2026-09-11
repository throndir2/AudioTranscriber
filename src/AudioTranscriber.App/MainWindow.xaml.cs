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
