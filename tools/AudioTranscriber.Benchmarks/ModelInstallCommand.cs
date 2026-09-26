using System.Globalization;
using AudioTranscriber.Providers;

namespace AudioTranscriber.Benchmarks;

public static class ModelInstallCommand
{
    internal delegate Task<string> ModelInstaller(LocalWhisperModel model, string directory,
        bool explicitlyApproved, IProgress<long>? downloadedBytes, CancellationToken cancellationToken);

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default) =>
        RunAsync(args, output, error, VerifiedModelDownload.InstallWhisperAsync, cancellationToken);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        ModelInstaller installer, CancellationToken cancellationToken = default)
    {
        InstallOptions options;
        try { options = InstallOptions.Parse(args); }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("Invalid model-install options. Use install-model --help. No download started.");
            return 6;
        }
        if (options.Help)
        {
            await output.WriteLineAsync("install-model --list");
            await output.WriteLineAsync("install-model --model tiny|base|small|large-v3|large-v3-turbo --directory PATH");
            await output.WriteLineAsync("Add --accept-model-license --accept-download-bytes EXACT_BYTES only after reviewing the model disclosure.");
            await output.WriteLineAsync("No model is downloaded without both acknowledgments. This command never reads NVIDIA credentials or sends audio.");
            return 0;
        }
        if (options.List)
        {
            foreach (var entry in LocalWhisperModelCatalog.All) await DiscloseAsync(entry, output);
            return 0;
        }
        var model = LocalWhisperModelCatalog.All.SingleOrDefault(m => m.Id == options.ModelId);
        if (model is null)
        {
            await error.WriteLineAsync("Select a catalog model with --model; use install-model --list to review sizes and licenses.");
            return 2;
        }
        await DiscloseAsync(model, output);
        if (!options.AcceptLicense || options.AcceptBytes != model.Bytes)
        {
            await error.WriteLineAsync("Download not authorized. Explicitly pass --accept-model-license and --accept-download-bytes " +
                model.Bytes.ToString(CultureInfo.InvariantCulture) + " to acknowledge this exact model size.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            await error.WriteLineAsync("An explicit destination --directory is required. No download started.");
            return 2;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var progress = new InstallProgress(output, model.Bytes);
        var path = await installer(model, options.Directory, true, progress, cancellationToken);
        await output.WriteLineAsync("Model installed and SHA256 verified: " + Path.GetFileName(path));
        await output.WriteLineAsync("The adjacent .LICENSE.txt retains the license and source/hash. Select this local model file in the app.");
        return 0;
    }

    private static async Task DiscloseAsync(LocalWhisperModel model, TextWriter output)
    {
        await output.WriteLineAsync($"Model: {model.Id} ({model.FileName})");
        await output.WriteLineAsync($"Download size: {model.Bytes.ToString(CultureInfo.InvariantCulture)} bytes " +
            $"({(model.Bytes / 1024d / 1024 / 1024).ToString("F3", CultureInfo.InvariantCulture)} GiB); disk and inference memory are additional requirements.");
        await output.WriteLineAsync("License: " + model.License);
        await output.WriteLineAsync("License source: " + model.LicenseUri);
        await output.WriteLineAsync("Pinned model source: " + model.DownloadUri);
        await output.WriteLineAsync("Required SHA256: " + model.Sha256);
        if (model.Bytes > 1024L * 1024 * 1024)
            await output.WriteLineAsync("Large download: exceeds 1 GiB. This is optional, never needed to start the app.");
    }

    private sealed class InstallProgress(TextWriter output, long total) : IProgress<long>
    {
        private int lastStep = -1;
        public void Report(long value)
        {
            var step = (int)(value * 100 / total) / 10;
            if (step <= lastStep) return;
            lastStep = step;
            output.WriteLine($"Model download: {step * 10}% ({value.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)} bytes).");
        }
    }

    private sealed record InstallOptions(string? ModelId, string? Directory, bool AcceptLicense,
        long? AcceptBytes, bool List, bool Help)
    {
        public static InstallOptions Parse(string[] args)
        {
            string? model = null, directory = null;
            bool license = false, list = false, help = false;
            long? bytes = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!seen.Add(args[i])) throw new ArgumentException("Duplicate option.");
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "--model": model = Value().ToLowerInvariant(); break;
                    case "--directory": directory = Value(); break;
                    case "--accept-model-license": license = true; break;
                    case "--accept-download-bytes":
                        if (!long.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                            throw new ArgumentException("Invalid byte acknowledgment.");
                        bytes = parsed;
                        break;
                    case "--list": list = true; break;
                    case "--help": help = true; break;
                    default: throw new ArgumentException("Unknown option.");
                }
            }
            if ((list || help) && seen.Count != 1) throw new ArgumentException("Listing/help cannot install.");
            return new(model, directory, license, bytes, list, help);
        }
    }
}
