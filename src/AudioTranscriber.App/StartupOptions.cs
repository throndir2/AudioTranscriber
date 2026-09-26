namespace AudioTranscriber.App;

public enum McpMode { None, Headless, Ui }

public sealed record StartupOptions(string DataRoot, bool Smoke, McpMode Mcp = McpMode.None, bool DataRootSpecified = false)
{
    public static StartupOptions Parse(IReadOnlyList<string> arguments)
    {
        string? root = null;
        var smoke = false;
        var mcp = McpMode.None;
        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--data-root" when i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal):
                    if (root is not null) throw new ArgumentException("Specify --data-root only once.");
                    root = arguments[++i];
                    if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The data root cannot be empty.");
                    break;
                case "--smoke":
                    smoke = true;
                    break;
                case "--mcp" or "--mcp-ui":
                    if (mcp != McpMode.None) throw new ArgumentException("Specify only one of --mcp and --mcp-ui.");
                    mcp = arguments[i] == "--mcp" ? McpMode.Headless : McpMode.Ui;
                    break;
                default:
                    throw new ArgumentException("Supported options: --data-root PATH, --smoke, --mcp, and --mcp-ui. Never pass credentials as arguments.");
            }
        }
        if (smoke && root is null) throw new ArgumentException("--smoke requires an explicit isolated --data-root PATH.");
        if (smoke && mcp != McpMode.None) throw new ArgumentException("--smoke cannot be combined with an MCP mode.");
        var specified = root is not null;
        // MCP sessions never touch the personal library unless a data root is named explicitly.
        root ??= mcp == McpMode.None
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioTranscriber")
            : Path.Combine(Path.GetTempPath(), "AudioTranscriber-mcp", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        return new(Path.GetFullPath(root), smoke, mcp, specified);
    }
}
