using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AudioTranscriber.Core;
using Whisper.net;

namespace AudioTranscriber.Providers;

public sealed class LocalWhisperProvider : ITranscriptionProvider
{
    private readonly string modelPath;
    private readonly bool useGpu;
    private readonly SemaphoreSlim gate = new(1, 1);
    private WhisperFactory? factory;
    private string? modelHash;
    private string runtime = "not-loaded";
    private bool disposed;
    public ProviderDescriptor Descriptor { get; } = new(
        "local-whisper", "Local Whisper (optional model)", "user-installed-whisper.cpp",
        false, TimingGranularity.Segment, 30, DefaultLanguage: "en");

    public LocalWhisperProvider(string modelPath, bool useGpu = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        this.modelPath = Path.GetFullPath(modelPath);
        this.useGpu = useGpu;
    }

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            request.Validate();
            var language = request.Language.ToLowerInvariant();
            if (language.Length is < 2 or > 3 || language.Any(c => c is < 'a' or > 'z'))
                throw new ProviderException(ProviderFailureKind.InvalidRequest, "unsupported-language");
            if (!File.Exists(modelPath))
                throw new ProviderException(ProviderFailureKind.LocalModelMissing, "local-model-missing");
            var pcm = await Pcm16Audio.ReadAsync(request.AudioPath, request.SampleCount, cancellationToken);
            if (factory is null)
            {
                await using var file = File.OpenRead(modelPath);
                if (file.Length is < 1024 or > 4L * 1024 * 1024 * 1024)
                    throw new ProviderException(ProviderFailureKind.LocalModelMissing, "local-model-invalid");
                modelHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
                var catalogModel = LocalWhisperModelCatalog.All.FirstOrDefault(m => m.FileName == Path.GetFileName(modelPath));
                if (catalogModel is not null && (catalogModel.Bytes != file.Length || catalogModel.Sha256 != modelHash))
                    throw new ProviderException(ProviderFailureKind.LocalModelMissing, "local-model-integrity-mismatch");
                factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu, UseFlashAttention = useGpu });
                runtime = Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
            }
            if (!WhisperFactory.GetSupportedLanguages().Contains(language))
                throw new ProviderException(ProviderFailureKind.InvalidRequest, "unsupported-language");
            using var processor = factory.CreateBuilder()
                .WithLanguage(language).WithNoContext()
                .WithThreads(Math.Max(1, Math.Min(Environment.ProcessorCount, 8))).Build();
            var segments = ImmutableArray.CreateBuilder<TranscriptionSegment>();
            var evidence = new List<object>();
            var diagnostics = ImmutableArray.CreateBuilder<string>();
            var maximumMs = (request.SampleCount * 1000 + 15999) / 16000;
            await foreach (var segment in processor.ProcessAsync(Pcm16Audio.ToFloatSamples(pcm), cancellationToken))
            {
                var (confidence, lowestToken, textTokens) = SegmentConfidence(segment.Tokens);
                evidence.Add(new { segment.Text, StartMilliseconds = segment.Start.TotalMilliseconds,
                    EndMilliseconds = segment.End.TotalMilliseconds, Confidence = confidence,
                    LowestTokenProbability = lowestToken, TextTokens = textTokens, segment.NoSpeechProbability });
                if (string.IsNullOrWhiteSpace(segment.Text)) continue;
                var start = (long)segment.Start.TotalMilliseconds;
                var end = (long)segment.End.TotalMilliseconds;
                if (start < 0 || end < start || end > maximumMs)
                {
                    diagnostics.Add("local-segment-timing-invalid");
                    segments.Add(new(segment.Text, 0, maximumMs, TimingGranularity.Chunk, [], confidence));
                }
                else segments.Add(new(segment.Text, start, end, TimingGranularity.Segment, [], confidence));
            }
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add("word-timing-unavailable");
            diagnostics.Add("whisper-runtime:" + runtime);
            return new(Descriptor.Id, "whisper.cpp:sha256:" + modelHash,
                segments.Count == 0 ? TranscriptionStatus.Empty : TranscriptionStatus.Succeeded,
                segments.ToImmutable(), JsonSerializer.Serialize(evidence), diagnostics.ToImmutable());
        }
        catch (ProviderException error) { throw error.ToContractException(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { throw new ProviderException(ProviderFailureKind.InvalidRequest, "local-audio-or-model-invalid").ToContractException(); }
        catch (Exception)
        { throw new ProviderException(ProviderFailureKind.LocalModelMissing, "local-inference-unavailable").ToContractException(); }
        finally { gate.Release(); }
    }

    /// <summary>Mean decoder probability of the segment's text tokens; whisper.cpp control tokens look like "[_BEG_]".</summary>
    internal static (double? Mean, double? Lowest, int Count) SegmentConfidence(IEnumerable<WhisperToken>? tokens)
    {
        var probabilities = (tokens ?? []).Where(token => token is not null &&
                !(token.Text is { } text && text.StartsWith("[_", StringComparison.Ordinal) && text.EndsWith(']')) &&
                float.IsFinite(token.Probability) && token.Probability is >= 0 and <= 1)
            .Select(token => (double)token.Probability).ToArray();
        return probabilities.Length == 0 ? (null, null, 0)
            : (Math.Round(probabilities.Average(), 4), Math.Round(probabilities.Min(), 4), probabilities.Length);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) factory?.Dispose(); disposed = true; }
        finally { gate.Release(); }
    }
}
