using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioTranscriber.Core;
using AudioTranscriber.Providers;

namespace AudioTranscriber.Benchmarks;

public sealed record BenchmarkAttempt(
    int? AttemptNumber, string ProviderId, string ClipId, int RetryIndex, string Status,
    string? FailureCategory, string Reference, string? Hypothesis, double AudioSeconds,
    double? LatencySeconds, double? RealTimeFactor, string? ObservedModel,
    TranscriptMetrics? Metrics, int WordCount, int MissingWordTimingSegments,
    int InvalidTimingDiagnostics, IReadOnlyList<string> Diagnostics,
    IReadOnlyList<TranscriptionSegment> Segments, string? RawResponse);

public sealed class BenchmarkRunner
{
    private readonly Func<NvidiaModel, ICloudConsentStore, ITranscriptionProvider> providerFactory;
    private readonly RequestBudget budget = new();
    private readonly List<BenchmarkAttempt> attempts = [];
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    public BenchmarkRunner(Func<NvidiaModel, ICloudConsentStore, ITranscriptionProvider> providerFactory) =>
        this.providerFactory = providerFactory;

    public async Task<IReadOnlyList<BenchmarkAttempt>> RunAsync(IReadOnlyList<BenchmarkClip> clips,
        string outputDirectory, bool publicAudioCloudApproved, int maximumRetries = 0,
        CancellationToken cancellationToken = default)
    {
        if (!publicAudioCloudApproved || maximumRetries is < 0 or > 1 ||
            clips.Count != 2 || !clips.Select(c => c.Id).SequenceEqual(PublicFixture.ClipIds) ||
            clips.Any(c => c.SampleCount is <= 0 or > 240000))
            throw new InvalidOperationException("A bounded, explicitly approved public fixture is required.");
        Directory.CreateDirectory(outputDirectory);
        var sessionId = Guid.NewGuid();
        var tracks = clips.ToDictionary(c => c.Id, _ => Guid.NewGuid());
        var consent = new PublicFixtureConsent(sessionId, tracks.Values.ToImmutableArray());
        await SaveAsync(clips, outputDirectory, maximumRetries, cancellationToken);
        foreach (var model in NvidiaModelCatalog.All)
        {
            await using var provider = providerFactory(model, consent);
            var stopped = false;
            foreach (var clip in clips)
            {
                if (stopped)
                {
                    attempts.Add(Failure(model, clip, null, 0, "Skipped", "endpoint-stopped", null));
                    await SaveAsync(clips, outputDirectory, maximumRetries, cancellationToken);
                    continue;
                }
                for (var retry = 0; retry <= maximumRetries; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { budget.CountBeforeAttempt(TimeSpan.FromSeconds(clip.DurationSeconds)); }
                    catch (InvalidOperationException)
                    {
                        attempts.Add(Failure(model, clip, null, retry, "Skipped", "budget-exhausted", null));
                        stopped = true;
                        break;
                    }
                    var attemptNumber = budget.Attempts;
                    var watch = Stopwatch.StartNew();
                    var retryable = false;
                    try
                    {
                        var result = await provider.TranscribeAsync(new(sessionId, tracks[clip.Id], Guid.NewGuid(),
                            clip.PcmPath, clip.SampleCount, "en", 0), cancellationToken);
                        watch.Stop();
                        var hypothesis = string.Join(" ", result.Segments.Select(s => s.Text));
                        var diagnostics = result.Diagnostics.IsDefault ? [] : result.Diagnostics.ToArray();
                        attempts.Add(new(attemptNumber, model.Id, clip.Id, retry, result.Status.ToString(), null,
                            clip.Reference, hypothesis, clip.DurationSeconds, watch.Elapsed.TotalSeconds,
                            watch.Elapsed.TotalSeconds / clip.DurationSeconds, result.ObservedModel,
                            ErrorMetrics.Compare(clip.Reference, hypothesis),
                            result.Segments.Sum(s => s.Words.IsDefault ? 0 : s.Words.Length),
                            result.Segments.Count(s => s.Words.IsDefaultOrEmpty),
                            diagnostics.Count(d => d.Contains("invalid", StringComparison.Ordinal)),
                            diagnostics, result.Segments.ToArray(), result.RawResponse));
                    }
                    catch (TranscriptionProviderException error)
                    {
                        watch.Stop();
                        stopped = error.Error.Code is ProviderErrorCode.Authentication or ProviderErrorCode.PermissionDenied
                            or ProviderErrorCode.QuotaExceeded or ProviderErrorCode.RateLimited;
                        retryable = error.Error.IsTransient && !stopped;
                        attempts.Add(Failure(model, clip, attemptNumber, retry, "Failed",
                            error.Error.Code.ToString(), watch.Elapsed.TotalSeconds));
                    }
                    catch (OperationCanceledException)
                    {
                        attempts.Add(Failure(model, clip, attemptNumber, retry, "Canceled", "canceled", watch.Elapsed.TotalSeconds));
                        await SaveAsync(clips, outputDirectory, maximumRetries, CancellationToken.None);
                        throw;
                    }
                    catch (Exception)
                    {
                        attempts.Add(Failure(model, clip, attemptNumber, retry, "Failed",
                            "sanitized-unexpected-failure", watch.Elapsed.TotalSeconds));
                        stopped = true;
                    }
                    await SaveAsync(clips, outputDirectory, maximumRetries, cancellationToken);
                    if (!retryable || retry == maximumRetries) break;
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
        await SaveAsync(clips, outputDirectory, maximumRetries, cancellationToken);
        return attempts.AsReadOnly();
    }

    private static BenchmarkAttempt Failure(NvidiaModel model, BenchmarkClip clip, int? number,
        int retry, string status, string category, double? latency) =>
        new(number, model.Id, clip.Id, retry, status, category, clip.Reference, null, clip.DurationSeconds,
            latency, latency / clip.DurationSeconds, null, null, 0, 0, 0, [], [], null);

    private async Task SaveAsync(IReadOnlyList<BenchmarkClip> clips, string directory,
        int maximumRetries, CancellationToken cancellationToken)
    {
        var finalResults = attempts.GroupBy(a => (a.ProviderId, a.ClipId)).Select(g => g.Last()).ToArray();
        var matched = clips.Where(c => NvidiaModelCatalog.All.All(m =>
            finalResults.Any(a => a.ProviderId == m.Id && a.ClipId == c.Id && a.Status == "Succeeded")))
            .Select(c => c.Id).ToArray();
        var summaries = NvidiaModelCatalog.All.Select(model =>
        {
            var rows = finalResults.Where(a => a.ProviderId == model.Id).ToArray();
            var comparable = rows.Where(a => matched.Contains(a.ClipId) && a.Status == "Succeeded").ToArray();
            return new
            {
                ProviderId = model.Id,
                Availability = rows.Any(a => a.Status is "Succeeded" or "Empty" or "Partial") ? "responded"
                    : rows.Any(a => a.Status == "Failed") ? "failed" : "not-attempted",
                PlannedClips = clips.Count, Successful = rows.Count(a => a.Status == "Succeeded"),
                Empty = rows.Count(a => a.Status == "Empty"), Partial = rows.Count(a => a.Status == "Partial"),
                Failed = rows.Count(a => a.Status == "Failed"), Skipped = rows.Count(a => a.Status == "Skipped"),
                SuccessfulCoverage = (double)rows.Count(a => a.Status == "Succeeded") / clips.Count,
                MatchedClipIds = matched,
                MatchedWer = MicroRate(comparable, word: true),
                MatchedCer = MicroRate(comparable, word: false),
                MeanSuccessfulWallLatencySeconds = Mean(rows.Where(a => a.Status == "Succeeded").Select(a => a.LatencySeconds)),
                MeanSuccessfulRealTimeFactor = Mean(rows.Where(a => a.Status == "Succeeded").Select(a => a.RealTimeFactor))
            };
        }).ToArray();
        var report = new
        {
            SchemaVersion = 1, StartedUtc = started, UpdatedUtc = DateTimeOffset.UtcNow,
            Fixture = new { PublicFixture.Dataset, PublicFixture.Revision, PublicFixture.Sha256,
                Url = PublicFixture.DownloadUri, PublicFixture.MaximumDownloadBytes, PublicFixture.Attribution },
            Clips = clips.Select(c => new { c.Id, c.Reference, c.SampleCount, c.DurationSeconds,
                c.SourceSha256, c.PcmSha256, c.NormalizerVersion }),
            Settings = new { ProviderAuthority = NvidiaRivaProvider.Authority, Method = "unary Recognize",
                Encoding = "signed little-endian PCM16", SampleRate = 16000, Channels = 1, SourceLanguage = "en",
                Translation = false, ModelConfigField = "unset", Concurrency = 1, MaximumRetries = maximumRetries,
                RequestDeadlineSeconds = 60, MaxAlternatives = 1, Punctuation = true, Verbatim = true,
                ProfanityFilter = false, NativeDiarization = false, WordOffsets = "Parakeet only",
                LatencyScope = "adapter wall time including in-process concurrency gate and conservative pacing" },
            Budget = new { budget.Attempts, budget.SubmittedAudioSeconds,
                RequestBudget.MaximumAttempts, RequestBudget.MaximumAudioSeconds },
            Catalog = NvidiaModelCatalog.All,
            ErrorMetrics.Normalization,
            Interpretation = "Compare only matched successful clips with coverage. Failed/skipped results have no WER; " +
                "empty/partial hypotheses remain visible but are not matched successes. Same-speaker clean read English, n=2, " +
                "no repeated latency trials. Cannot establish a D&D winner, named-entity accuracy, or diarization DER.",
            Runtime = new { Dotnet = Environment.Version.ToString(), Tool = typeof(BenchmarkRunner).Assembly.GetName().Version?.ToString() },
            Summaries = summaries, Attempts = attempts
        };
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        var path = Path.Combine(directory, "results.json");
        await File.WriteAllTextAsync(path + ".partial", JsonSerializer.Serialize(report, options), cancellationToken);
        File.Move(path + ".partial", path, true);
        var csv = new StringBuilder("attempt,provider,clip,retry,status,failure,audio_seconds,latency_seconds,rtf,wer,cer,words,missing_word_timing_segments,invalid_timing_diagnostics,reference,hypothesis\r\n");
        foreach (var row in attempts)
        {
            csv.AppendLine(string.Join(",", new object?[]
            {
                row.AttemptNumber, row.ProviderId, row.ClipId, row.RetryIndex, row.Status, row.FailureCategory,
                row.AudioSeconds, row.LatencySeconds, row.RealTimeFactor, row.Metrics?.Words.Rate,
                row.Metrics?.Characters.Rate, row.WordCount, row.MissingWordTimingSegments,
                row.InvalidTimingDiagnostics, row.Reference, row.Hypothesis
            }.Select(CsvCell)));
        }
        path = Path.Combine(directory, "results.csv");
        await File.WriteAllTextAsync(path + ".partial", csv.ToString(), cancellationToken);
        File.Move(path + ".partial", path, true);
    }

    public static string CsvCell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static double? MicroRate(IEnumerable<BenchmarkAttempt> rows, bool word)
    {
        var counts = rows.Select(a => word ? a.Metrics!.Words : a.Metrics!.Characters).ToArray();
        var units = counts.Sum(c => c.ReferenceUnits);
        return units == 0 ? null : (double)counts.Sum(c => c.Errors) / units;
    }
    private static double? Mean(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }

    private sealed class PublicFixtureConsent(Guid sessionId, ImmutableArray<Guid> tracks) : ICloudConsentStore
    {
        public Task<CloudConsent?> GetAsync(Guid requestedSessionId, string providerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CloudConsent?>(requestedSessionId == sessionId &&
                NvidiaModelCatalog.All.Any(m => m.Id == providerId)
                ? new(sessionId, providerId, tracks, ConsentState.Granted, DateTimeOffset.UtcNow, "public-fixture-nvidia-v1")
                : null);
        }
    }
}
