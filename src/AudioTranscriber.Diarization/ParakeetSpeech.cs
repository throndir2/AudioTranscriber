using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using AudioTranscriber.Core;
using ICSharpCode.SharpZipLib.BZip2;
using SherpaOnnx;

namespace AudioTranscriber.Diarization;

/// <summary>NVIDIA Parakeet TDT 0.6B v3 (CC BY 4.0), int8 ONNX export from sherpa-onnx, for CPU inference.</summary>
public static class ParakeetModels
{
    public const string ArchiveName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
    public const string ArchiveUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/" + ArchiveName + ".tar.bz2";
    public const long ArchiveBytes = 487_170_055;
    public const string ArchiveSha256 = "5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf";
    public const long ExtractedBytes = 671_239_000;
    public const string License = "CC BY 4.0 (NVIDIA parakeet-tdt-0.6b-v3); int8 ONNX export by the sherpa-onnx project (Apache-2.0).";

    // (file, bytes, sha256) of the files the recognizer loads.
    internal static readonly (string Name, long Bytes, string Sha256)[] Files =
    [
        ("encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
        ("decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
        ("joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
        ("tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d")
    ];

    public static string ModelDirectory(string parent) => Path.Combine(parent, ArchiveName);

    /// <summary>True when every model file is present with its pinned size (hashes are checked at install time).</summary>
    public static bool IsInstalled(string parent) => Files.All(file =>
        new FileInfo(Path.Combine(ModelDirectory(parent), file.Name)) is { Exists: true } info && info.Length == file.Bytes);

    public static async Task<string> InstallAsync(string parent, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        parent = LocalPaths.RequireDirectoryPath(parent);
        Directory.CreateDirectory(parent);
        var target = ModelDirectory(parent);
        if (IsInstalled(parent)) return target;
        var archive = Path.Combine(parent, ArchiveName + ".tar.bz2");
        using (var client = new HttpClient { Timeout = TimeSpan.FromHours(2) })
        {
            var megabytes = ArchiveBytes / 1_048_576;
            await DiarizationModels.DownloadAsync(client, new Uri(ArchiveUrl), archive, ArchiveBytes, ArchiveSha256,
                new InlineProgress<long>(bytes => progress?.Report(
                    $"Downloading the Parakeet transcription model: {bytes * 100 / ArchiveBytes}% ({bytes / 1_048_576:N0} / {megabytes:N0} MiB)…")),
                cancellationToken, maximumBytes: ArchiveBytes);
        }
        progress?.Report("Unpacking the Parakeet transcription model (about a minute)…");
        var stage = Path.Combine(parent, ".extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(async () =>
            {
                await using var file = File.OpenRead(archive);
                using var decompressed = new BZip2InputStream(file);
                await DiarizationModels.ExtractSafeTarAsync(decompressed, stage, cancellationToken, ArchiveName, ExtractedBytes + 1_048_576);
            }, cancellationToken);
            var staged = Path.Combine(stage, ArchiveName);
            foreach (var (name, bytes, sha256) in Files)
                await DiarizationModels.VerifyFileAsync(Path.Combine(staged, name), bytes, sha256, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staged, "NOTICE.txt"),
                "NVIDIA Parakeet TDT 0.6B v3, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3\n" +
                "License: Creative Commons Attribution 4.0 International (CC BY 4.0), https://creativecommons.org/licenses/by/4.0/\n" +
                "Converted to int8 ONNX by the sherpa-onnx project (Apache-2.0): " + ArchiveUrl + "\n", cancellationToken);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staged, target);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (File.Exists(archive)) File.Delete(archive);
        }
        return target;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>
/// Parakeet TDT on the CPU through sherpa-onnx. Audio never leaves the PC. Returns word timestamps and a per-segment
/// confidence (mean token probability) read from the native result, which the managed wrapper does not expose.
/// </summary>
public sealed class SherpaParakeetProvider : ITranscriptionProvider
{
    public const string ProviderId = "local-parakeet";
    // Parakeet TDT 0.6B v3 languages; it detects which one is spoken.
    public static readonly ImmutableHashSet<string> Languages = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt", "mt", "pl", "pt", "ro",
        "sk", "sl", "es", "sv", "ru", "uk");
    private readonly string modelDirectory;
    private readonly int threads;
    private readonly SemaphoreSlim gate = new(1, 1);
    private OfflineRecognizer? recognizer;
    private bool disposed;

    public ProviderDescriptor Descriptor { get; } = new(ProviderId, "Parakeet TDT v3 (local CPU)", ParakeetModels.ArchiveName,
        false, TimingGranularity.Word, 30, DefaultLanguage: "en");

    public SherpaParakeetProvider(string modelsParent, int? threads = null)
    {
        modelDirectory = ParakeetModels.ModelDirectory(Path.GetFullPath(modelsParent));
        // ONNX Runtime's worker threads spin: 4 threads is ~1.4x faster than 2 but uses ~2x the CPU (measured).
        this.threads = threads ?? Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
    }

    public static bool SupportsLanguage(string language) =>
        Languages.Contains(language.Split('-', 2)[0]);

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try { request.Validate(Descriptor.MaximumAudioSeconds); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw Failure(ProviderErrorCode.InvalidAudio, "The audio chunk is not valid for Parakeet."); }
        if (!SupportsLanguage(request.Language))
            throw Failure(ProviderErrorCode.UnsupportedLanguage,
                "Parakeet supports 25 European languages. Choose Local Whisper for this session's language.");
        if (!File.Exists(Path.Combine(modelDirectory, "encoder.int8.onnx")))
            throw Failure(ProviderErrorCode.ModelUnavailable, "The Parakeet model is not installed yet; it downloads automatically.");
        byte[] pcm;
        try { pcm = await Pcm16Audio.ReadAsync(request.AudioPath, request.SampleCount, cancellationToken); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { throw Failure(ProviderErrorCode.InvalidAudio, "The audio chunk could not be read."); }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var samples = Pcm16Audio.ToFloatSamples(pcm);
            var json = await Task.Run(() => Decode(samples), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Parse(json, request.SampleCount);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
            SEHException or InvalidOperationException or JsonException)
        { throw Failure(ProviderErrorCode.ModelUnavailable, "Parakeet could not run on this PC: " + error.GetType().Name); }
        finally { gate.Release(); }
    }

    private string Decode(float[] samples)
    {
        recognizer ??= CreateRecognizer();
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        recognizer.Decode(stream);
        var pointer = NativeMethods.SherpaOnnxGetOfflineStreamResultAsJson(stream.Handle);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("no-result");
        try { return Marshal.PtrToStringUTF8(pointer) ?? throw new InvalidOperationException("no-result"); }
        finally { NativeMethods.SherpaOnnxDestroyOfflineStreamResultJson(pointer); }
    }

    private OfflineRecognizer CreateRecognizer()
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    internal TranscriptionResult Parse(string json, long sampleCount)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = root.GetProperty("text").GetString()?.Trim() ?? "";
        var tokens = root.GetProperty("tokens").EnumerateArray().Select(token => token.GetString() ?? "").ToArray();
        var starts = Floats(root, "timestamps");
        var durations = Floats(root, "durations");
        var logProbabilities = Floats(root, "ys_log_probs");
        var durationMs = (sampleCount * 1000 + 15999) / 16000;
        var words = ImmutableArray.CreateBuilder<TranscriptionWord>();
        var tokenProbabilities = new List<double>();
        string current = "";
        long wordStart = 0, wordEnd = 0;
        var wordProbabilities = new List<double>();
        void Flush()
        {
            if (current.Trim().Length > 0)
                words.Add(new(current.Trim(), wordStart, Math.Max(wordStart, wordEnd),
                    wordProbabilities.Count == 0 ? null : Math.Round(wordProbabilities.Average(), 4)));
            current = "";
            wordProbabilities.Clear();
        }
        for (var i = 0; i < tokens.Length; i++)
        {
            var start = i < starts.Length ? (long)Math.Round(starts[i] * 1000) : wordEnd;
            var end = start + (i < durations.Length ? (long)Math.Round(durations[i] * 1000) : 0);
            start = Math.Clamp(start, 0, durationMs);
            end = Math.Clamp(end, start, durationMs);
            if (tokens[i].StartsWith(' ') || current.Length == 0)
            {
                Flush();
                wordStart = start;
            }
            current += tokens[i];
            wordEnd = end;
            if (i < logProbabilities.Length && double.IsFinite(logProbabilities[i]))
            {
                var probability = Math.Exp(logProbabilities[i]);
                tokenProbabilities.Add(probability);
                wordProbabilities.Add(probability);
            }
        }
        Flush();
        double? confidence = tokenProbabilities.Count == 0 ? null : Math.Round(tokenProbabilities.Average(), 4);
        var segments = text.Length == 0 || words.Count == 0 ? ImmutableArray<TranscriptionSegment>.Empty :
            [new TranscriptionSegment(text, words[0].StartMilliseconds, words[^1].EndMilliseconds, TimingGranularity.Word,
                words.ToImmutable(), confidence)];
        var raw = JsonSerializer.Serialize(new
        {
            Text = text, Tokens = tokens, Timestamps = starts, Durations = durations, TokenProbabilities = tokenProbabilities
                .Select(value => Math.Round(value, 4)), Confidence = confidence, Language = root.TryGetProperty("lang", out var lang) ? lang.GetString() : null
        });
        return new(ProviderId, "sherpa-onnx:" + ParakeetModels.ArchiveName,
            segments.IsEmpty ? TranscriptionStatus.Empty : TranscriptionStatus.Succeeded, segments, raw, ["parakeet-runtime:cpu"]);
    }

    private static float[] Floats(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(value => value.GetSingle()).ToArray() : [];

    private static TranscriptionProviderException Failure(ProviderErrorCode code, string message) => new(new(code, message));

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) recognizer?.Dispose(); disposed = true; }
        finally { gate.Release(); }
    }

    private static class NativeMethods
    {
        [DllImport("sherpa-onnx-c-api", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxGetOfflineStreamResultAsJson(IntPtr stream);

        [DllImport("sherpa-onnx-c-api", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDestroyOfflineStreamResultJson(IntPtr json);
    }
}
