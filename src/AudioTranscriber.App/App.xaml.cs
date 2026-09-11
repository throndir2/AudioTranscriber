using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AudioTranscriber.Application;
using AudioTranscriber.Storage;

namespace AudioTranscriber.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IAppController? controller = null;
        StartupOptions? options = null;
        try
        {
            options = StartupOptions.Parse(e.Args);
            if (options.Smoke && new LibraryStore(options.DataRoot).GetSessions(1).Count != 0)
                throw new ArgumentException("Smoke mode requires an isolated library with no sessions, so existing queued audio can never upload.");
            controller = new AppController(options.DataRoot);
            MainWindow? window = null;
            var dialogs = new DesktopDialogs(() => window!);
            var viewModel = new MainViewModel(controller, dialogs, Dispatcher);
            window = new MainWindow(viewModel) { SmokeMode = options.Smoke, IsEnabled = !options.Smoke };
            MainWindow = window;
            window.Show();
            await viewModel.InitializeAsync();
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (options.Smoke)
            {
                for (var tab = 0; tab < window.MainTabs.Items.Count; tab++)
                {
                    window.MainTabs.SelectedIndex = tab;
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                }
                window.MainTabs.SelectedIndex = 0;
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                var smoke = new
                {
                    loaded = window.IsLoaded && window.IsVisible,
                    windowTitle = window.Title,
                    outputDeviceCount = viewModel.OutputDevices.Count,
                    microphoneDeviceCount = viewModel.MicrophoneDevices.Count,
                    sessionCount = viewModel.Sessions.Count,
                    sqliteLoaded = true,
                    tabCount = window.MainTabs.Items.Count,
                    captureStarted = controller.IsRecording,
                    cloudUploadRequested = false,
                    checkedAtUtc = DateTimeOffset.UtcNow,
                    note = "Real WPF startup, endpoint enumeration and empty SQLite library only. No capture, playback, model download, Graph sign-in, or NVIDIA request."
                };
                await File.WriteAllTextAsync(Path.Combine(options.DataRoot, "app-smoke.json"),
                    JsonSerializer.Serialize(smoke, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
            }
        }
        catch (Exception error)
        {
            if (controller is not null)
            {
                try { await controller.DisposeAsync(); }
                catch { }
            }
            var message = error is ArgumentException
                ? error.Message
                : "The local application could not start. Check data-root permissions, available disk space, the Windows Desktop .NET 10 x64 runtime, and required native dependencies.";
            if (options?.Smoke == true)
            {
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(options.DataRoot, "app-smoke.json"),
                        JsonSerializer.Serialize(new
                        {
                            loaded = false, error = message, exceptionType = error.GetType().FullName,
                            error.HResult, detail = error.Message, innerDetail = error.InnerException?.Message
                        }));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else if (!e.Args.Contains("--smoke", StringComparer.Ordinal))
                MessageBox.Show(message, "AudioTranscriber startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
