using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.X11;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using AudioTranscriber.App.Mcp;
using AudioTranscriber.Application;

namespace AudioTranscriber.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var options = StartupOptions.Parse(args);
            if (options.Mcp == McpMode.Headless) return RunHeadlessMcpAsync(options).GetAwaiter().GetResult();
            if (options.Mcp == McpMode.Ui)
            {
                Console.Error.WriteLine("AudioTranscriber --mcp-ui moved to tools\\AudioTranscriber.UiMcp. Use scripts\\Start-Mcp.ps1 -Mode ui.");
                return 2;
            }
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "audiotranscriber" })
        .WithInterFont()
        .LogToTrace();

    private static async Task<int> RunHeadlessMcpAsync(StartupOptions options)
    {
        await using var controller = await Task.Run(() => new AppController(options.DataRoot));
        var tools = new HeadlessMcpTools(controller);
        await Task.Run(() => new McpStdioServer("audiotranscriber", HeadlessMcpTools.Instructions, tools.Tools)
            .RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None));
        return 0;
    }
}
