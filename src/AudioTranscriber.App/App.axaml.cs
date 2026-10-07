using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AudioTranscriber.Application;

namespace AudioTranscriber.App;

public partial class App : Avalonia.Application
{
    private MainViewModel? mainViewModel;
    private string[] startupArguments = [];
    private bool smokeMode = true;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var args = desktop.Args ?? [];
        desktop.Exit += (_, _) => { if (!smokeMode) mainViewModel?.ApplyPendingUpdate(startupArguments); };
        base.OnFrameworkInitializationCompleted();
        Dispatcher.UIThread.Post(() => _ = StartAsync(desktop, args));
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        IAppController? controller = null;
        StartupOptions? options = null;
        try
        {
            options = StartupOptions.Parse(args);
            AppDiagnostics.Start(options);
            if (options.Mcp == McpMode.Ui)
            {
                Console.Error.WriteLine("AudioTranscriber --mcp-ui moved to tools\\AudioTranscriber.UiMcp. Use scripts\\Start-Mcp.ps1 -Mode ui.");
                desktop.Shutdown(2);
                return;
            }
            startupArguments = args;
            controller = new AppController(options.DataRoot);
            if (options.Smoke && controller.Store.GetSessions(1).Count != 0)
                throw new ArgumentException("Smoke mode requires an isolated library with no sessions, so existing queued audio can never upload.");
            MainWindow? window = null;
            var dialogs = new DesktopDialogs(() => window!);
            var viewModel = new MainViewModel(controller, dialogs, Dispatcher.UIThread);
            window = new MainWindow(viewModel) { SmokeMode = options.Smoke, IsEnabled = !options.Smoke };
            desktop.MainWindow = window;
            window.Show();
            await viewModel.InitializeAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (!options.Smoke)
            {
                mainViewModel = viewModel;
                smokeMode = false;
                _ = viewModel.RunAutomaticSetupAsync();
                viewModel.StartUpdateChecks();
            }
            else
            {
                for (var tab = 0; tab < window.MainTabs.ItemCount; tab++)
                {
                    window.MainTabs.SelectedIndex = tab;
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
                window.MainTabs.SelectedIndex = 0;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var prerequisites = Prerequisites.Check();
                var smoke = new
                {
                    loaded = window.IsVisible,
                    windowTitle = window.Title,
                    outputDeviceCount = viewModel.OutputDevices.Count,
                    microphoneDeviceCount = viewModel.MicrophoneDevices.Count,
                    sessionCount = viewModel.Sessions.Count,
                    sqliteLoaded = true,
                    tabCount = window.MainTabs.ItemCount,
                    captureStarted = controller.IsRecording,
                    cloudUploadRequested = false,
                    ffmpegPath = prerequisites.FFmpeg,
                    ffprobePath = prerequisites.FFprobe,
                    vcRuntimeReady = prerequisites.VcRuntimeReady,
                    checkedAtUtc = DateTimeOffset.UtcNow,
                    note = "Real Avalonia startup, endpoint enumeration and empty SQLite library only. No capture, playback, model download, Graph sign-in, or NVIDIA request."
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
                : "The local application could not start. Check data-root permissions, available disk space, the .NET 10 x64 runtime, and required native dependencies.";
            if (options?.Smoke == true)
            {
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(options.DataRoot, "app-smoke.json"),
                        JsonSerializer.Serialize(new
                        {
                            loaded = false,
                            error = message,
                            exceptionType = error.GetType().FullName,
                            error.HResult,
                            detail = error.Message,
                            innerDetail = error.InnerException?.Message
                        }));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else if (args.Any(arg => arg.StartsWith("--mcp", StringComparison.Ordinal)))
                Console.Error.WriteLine($"AudioTranscriber MCP failed: {error.GetType().Name}: {error.Message}");
            else if (!args.Contains("--smoke", StringComparer.Ordinal))
                await DesktopDialogs.ShowMessageAsync("AudioTranscriber startup", message, NativeDialogIcon.Error);
            desktop.Shutdown(1);
        }
    }
}

