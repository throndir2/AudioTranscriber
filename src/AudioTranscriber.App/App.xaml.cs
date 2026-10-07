using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AudioTranscriber.App.Mcp;
using AudioTranscriber.Application;
using AudioTranscriber.Core;
using AudioTranscriber.Storage;

namespace AudioTranscriber.App;

public partial class App : System.Windows.Application
{
    private MainViewModel? mainViewModel;
    private string[] startupArguments = [];
    private bool smokeMode = true;

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"Exiting with code {e.ApplicationExitCode}.");
        if (!smokeMode) mainViewModel?.ApplyPendingUpdate(startupArguments);
        base.OnExit(e);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IAppController? controller = null;
        StartupOptions? options = null;
        try
        {
            options = StartupOptions.Parse(e.Args);
            AppDiagnostics.Start(options);
            if (options.Mcp != McpMode.None)
            {
                await RunMcpAsync(options);
                return;
            }
            startupArguments = e.Args;
            WindowTheme.Register();
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
            if (!options.Smoke)
            {
                mainViewModel = viewModel;
                smokeMode = false;
                _ = viewModel.RunAutomaticSetupAsync();
                viewModel.Discord.AutoConnect();
                viewModel.StartUpdateChecks();
            }
            if (options.Smoke)
            {
                for (var tab = 0; tab < window.MainTabs.Items.Count; tab++)
                {
                    window.MainTabs.SelectedIndex = tab;
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                }
                window.MainTabs.SelectedIndex = 0;
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                var prerequisites = Prerequisites.Check();
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
                    ffmpegPath = prerequisites.FFmpeg,
                    ffprobePath = prerequisites.FFprobe,
                    vcRuntimeReady = prerequisites.VcRuntimeReady,
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
            AppLog.Error("Startup failed.", error);
            if (controller is not null)
            {
                try { await controller.DisposeAsync(); }
                catch { }
            }
            var message = error is ArgumentException
                ? error.Message
                : "The local application could not start. Check data-root permissions, available disk space, the Windows Desktop .NET 10 x64 runtime, and required native dependencies." +
                  (AppLog.Directory is { } logs ? $"{Environment.NewLine}{Environment.NewLine}Details were written to the log in {logs}; attach it to a GitHub issue if this keeps happening." : "");
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
            else if (e.Args.Any(arg => arg.StartsWith("--mcp", StringComparison.Ordinal)))
                Console.Error.WriteLine($"AudioTranscriber MCP failed: {error.GetType().Name}: {error.Message}");
            else if (!e.Args.Contains("--smoke", StringComparer.Ordinal))
                MessageBox.Show(message, "AudioTranscriber startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Stdio MCP hooks. Headless hosts the real controller; UI drives a separate app window via UI Automation.</summary>
    private async Task RunMcpAsync(StartupOptions options)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        if (options.Mcp == McpMode.Headless)
        {
            var controller = await Task.Run(() => new AppController(options.DataRoot));
            try
            {
                var tools = new HeadlessMcpTools(controller);
                await Task.Run(() => new McpStdioServer("audiotranscriber", HeadlessMcpTools.Instructions, tools.Tools)
                    .RunAsync(input, output, CancellationToken.None));
            }
            finally { await controller.DisposeAsync(); }
        }
        else
        {
            UiMcpTools.EnableDpiAwareness();
            var tools = new UiMcpTools(options.DataRoot, options.DataRootSpecified);
            await Task.Run(() => new McpStdioServer("audiotranscriber-ui", UiMcpTools.Instructions, tools.Tools)
                .RunAsync(input, output, CancellationToken.None));
        }
        Shutdown(0);
    }
}
