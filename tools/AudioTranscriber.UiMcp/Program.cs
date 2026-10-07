using AudioTranscriber.App.Mcp;

namespace AudioTranscriber.App;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            UiMcpTools.EnableDpiAwareness();
            var parseArgs = args.Any(a => a == "--mcp-ui") ? args : args.Concat(["--mcp-ui"]).ToArray();
            var options = StartupOptions.Parse(parseArgs);
            var tools = new UiMcpTools(options.DataRoot, options.DataRootSpecified);
            await new McpStdioServer("audiotranscriber-ui", UiMcpTools.Instructions, tools.Tools)
                .RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None);
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"AudioTranscriber UI MCP failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }
}
