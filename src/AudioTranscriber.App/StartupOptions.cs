namespace AudioTranscriber.App;

public sealed record StartupOptions(string DataRoot, bool Smoke)
{
    public static StartupOptions Parse(IReadOnlyList<string> arguments)
    {
        string? root = null;
        var smoke = false;
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
                default:
                    throw new ArgumentException("Supported options: --data-root PATH and --smoke. Never pass credentials as arguments.");
            }
        }
        if (smoke && root is null) throw new ArgumentException("--smoke requires an explicit isolated --data-root PATH.");
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioTranscriber");
        return new(Path.GetFullPath(root), smoke);
    }
}
