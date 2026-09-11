using System.Text.Json;
using AudioTranscriber.Audio;
using AudioTranscriber.Providers;

namespace AudioTranscriber.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args.Length > 0 && args[0] == "install-model")
                return await ModelInstallCommand.RunAsync(args[1..], Console.Out, Console.Error, cancellation.Token);
            var options = Options.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("AudioTranscriber.Benchmarks --prepare-only [--output DIRECTORY] [--ffmpeg PATH] [--ffprobe PATH]");
                Console.WriteLine("AudioTranscriber.Benchmarks --approve-cloud --production-config PATH [--output DIRECTORY] [--max-retries 0|1]");
                Console.WriteLine("No API keys accepted in arguments. Public clips only; 6 initial, 9 maximum attempts, 135 seconds total.");
                Console.WriteLine("Optional local weights: install-model --list, then install-model --help for explicit license/size acknowledgment.");
                return 0;
            }
            if (!options.PrepareOnly && (!options.ApproveCloud || options.ProductionConfig is null))
            {
                Console.Error.WriteLine("Cloud mode requires --approve-cloud and --production-config PATH. Use --prepare-only without credentials.");
                return 2;
            }
            if (options.PrepareOnly && options.ProductionConfig is not null)
                throw new ArgumentException("Preparation must not read credentials.");
            var tools = new MediaTools(options.FFmpeg, options.FFprobe);
            var output = Path.GetFullPath(options.Output);
            var clips = await PublicFixture.PrepareAsync(Path.Combine(output, "fixture"), tools, cancellation.Token);
            var manifest = new
            {
                PublicFixture.Dataset, PublicFixture.Revision, PublicFixture.Sha256, PublicFixture.DownloadUri,
                PublicFixture.Attribution, PreparedUtc = DateTimeOffset.UtcNow,
                Clips = clips.Select(c => new { c.Id, c.Reference, c.DurationSeconds, c.SampleCount, c.SourceSha256, c.PcmSha256, c.NormalizerVersion })
            };
            await File.WriteAllTextAsync(Path.Combine(output, "fixture-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
            Console.WriteLine($"Public fixture verified: {clips.Count} complete mono 16 kHz utterances; {clips.Sum(c => c.DurationSeconds):F3} seconds.");
            if (options.PrepareOnly)
            {
                Console.WriteLine("Preparation complete. No credentials read, no cloud inference, no model weights downloaded.");
                return 0;
            }
            // The authorized path is opened only inside this process, after public audio validation.
            using var credentials = await ReadOnlyProductionCredentialLoader.LoadAsync(options.ProductionConfig!, cancellation.Token);
            var runner = new BenchmarkRunner((model, consent) => new NvidiaRivaProvider(model, credentials, consent));
            var attempts = await runner.RunAsync(clips, output, true, options.MaximumRetries, cancellation.Token);
            Console.WriteLine($"Comparison complete: {attempts.Count(a => a.AttemptNumber.HasValue)} attempts; " +
                $"{attempts.Count(a => a.Status == "Succeeded")} successful results. See results.json and results.csv.");
            Console.WriteLine("Clean read speech with one speaker does not establish a D&D or diarization winner.");
            return attempts.Any(a => a.Status is "Failed" or "Partial" or "Empty" or "Skipped") ? 3 : 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operation canceled; completed artifacts are retained."); return 130; }
        catch (ProviderException error)
        {
            Console.Error.WriteLine($"Operation stopped safely: {error.SafeCode}.");
            return 4;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Console.Error.WriteLine("Preparation, installation, or artifact IO failed. Check local paths, disk space, public download access, and media tools.");
            return 5;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Benchmark stopped safely: invalid arguments, fixture, or media configuration. No remote error body is printed.");
            return 6;
        }
    }

    private sealed record Options(string Output, bool PrepareOnly, bool ApproveCloud, string? ProductionConfig,
        int MaximumRetries, string FFmpeg, string FFprobe, bool Help)
    {
        public static Options Parse(string[] args)
        {
            string output = Path.Combine("tools", "AudioTranscriber.Benchmarks", "artifacts");
            string? config = null;
            string ffmpeg = "ffmpeg", ffprobe = "ffprobe";
            bool prepare = false, approved = false, help = false;
            int retries = 0;
            for (int index = 0; index < args.Length; index++)
            {
                string Value() => ++index < args.Length ? args[index] : throw new ArgumentException("Missing option value.");
                switch (args[index])
                {
                    case "--prepare-only": prepare = true; break;
                    case "--approve-cloud": approved = true; break;
                    case "--production-config": config = Value(); break;
                    case "--output": output = Value(); break;
                    case "--max-retries": retries = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--ffmpeg": ffmpeg = Value(); break;
                    case "--ffprobe": ffprobe = Value(); break;
                    case "--help": help = true; break;
                    default: throw new ArgumentException("Unknown option.");
                }
            }
            if (retries is < 0 or > 1) throw new ArgumentException("Retry limit is zero or one.");
            return new(output, prepare, approved, config, retries, ffmpeg, ffprobe, help);
        }
    }
}
