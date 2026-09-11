using System.Text.Json;
using AudioTranscriber.Benchmarks;
using AudioTranscriber.Core;
using AudioTranscriber.Providers;
using Xunit;

namespace AudioTranscriber.Providers.Tests;

public sealed class BenchmarkRunnerTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, "provider-test-data", Guid.NewGuid().ToString("N"));
    public BenchmarkRunnerTests() => Directory.CreateDirectory(directory);
    private static BenchmarkClip[] Clips =>
    [
        new("1272-128104-0000", "hello world", "synthetic.pcm", 16000, "synthetic-source", "synthetic-pcm", "mock"),
        new("1272-128104-0001", "again", "synthetic.pcm", 16000, "synthetic-source", "synthetic-pcm", "mock")
    ];

    [Fact]
    public async Task SixInitialAttemptsSameSettingsAndMatchedMetrics()
    {
        var requests = new List<(string Model, TranscriptionRequest Request)>();
        var runner = new BenchmarkRunner((model, consent) => new FakeProvider(model.Id, async request =>
        {
            var permission = await consent.GetAsync(request.SessionId, model.Id);
            Assert.True(permission!.Allows(request.SessionId, model.Id, request.TrackId));
            requests.Add((model.Id, request));
            return Result(model.Id, requests.Count % 2 == 1 ? "hello world" : "again");
        }));
        var rows = await runner.RunAsync(Clips, directory, true);
        Assert.Equal(6, rows.Count);
        Assert.Equal(6, requests.Count);
        Assert.All(requests, r => { Assert.Equal("en", r.Request.Language); Assert.Equal(16000, r.Request.SampleCount); });
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "results.json")));
        Assert.Equal(6, report.RootElement.GetProperty("Budget").GetProperty("Attempts").GetInt32());
        foreach (var summary in report.RootElement.GetProperty("Summaries").EnumerateArray())
        {
            Assert.Equal(1, summary.GetProperty("SuccessfulCoverage").GetDouble());
            Assert.Equal(0, summary.GetProperty("MatchedWer").GetDouble());
            Assert.Equal(2, summary.GetProperty("MatchedClipIds").GetArrayLength());
        }
    }

    [Fact]
    public async Task AuthAndQuotaStopEndpointAndNoRemoteBodyIsExported()
    {
        var calls = 0;
        var runner = new BenchmarkRunner((model, _) => new FakeProvider(model.Id, _ =>
        {
            calls++;
            throw new TranscriptionProviderException(new(ProviderErrorCode.QuotaExceeded, "PRIVATE REMOTE BODY"));
        }));
        var rows = await runner.RunAsync(Clips, directory, true, maximumRetries: 1);
        Assert.Equal(3, calls);
        Assert.Equal(3, rows.Count(r => r.Status == "Failed"));
        Assert.Equal(3, rows.Count(r => r.Status == "Skipped"));
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "results.json"));
        Assert.DoesNotContain("PRIVATE", json);
        Assert.All(rows, r => Assert.Null(r.Metrics));
    }

    [Fact]
    public async Task FailedAndEmptyClipsNeverImproveMatchedComparison()
    {
        var runner = new BenchmarkRunner((model, _) => new FakeProvider(model.Id, _ => Task.FromResult(
            model == NvidiaModelCatalog.Canary
                ? new TranscriptionResult(model.Id, null, TranscriptionStatus.Empty, [])
                : Result(model.Id, "hello world"))));
        await runner.RunAsync(Clips, directory, true);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "results.json")));
        foreach (var summary in report.RootElement.GetProperty("Summaries").EnumerateArray())
        {
            Assert.Equal(0, summary.GetProperty("MatchedClipIds").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("MatchedWer").ValueKind);
        }
    }

    [Fact]
    public async Task ExplicitRetriesCannotExceedNineIncludingFailures()
    {
        var calls = 0;
        var runner = new BenchmarkRunner((model, _) => new FakeProvider(model.Id, _ =>
        {
            calls++;
            throw new TranscriptionProviderException(new(ProviderErrorCode.Unavailable, "safe", true));
        }));
        var rows = await runner.RunAsync(Clips, directory, true, maximumRetries: 1);
        Assert.Equal(9, calls);
        Assert.Equal(9, rows.Count(r => r.AttemptNumber.HasValue));
        Assert.Contains(rows, r => r.FailureCategory == "budget-exhausted");
    }

    [Fact]
    public async Task NoCloudApprovalMeansNoCallsOrArtifacts()
    {
        var runner = new BenchmarkRunner((_, _) => throw new InvalidOperationException("must not create provider"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Clips, directory, false));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://invalid\")")]
    [InlineData("  +malicious")]
    [InlineData("@function")]
    [InlineData("-formula")]
    public void CsvNeutralizesFormulaCells(string text) => Assert.StartsWith("\"'", BenchmarkRunner.CsvCell(text));

    private static TranscriptionResult Result(string id, string text) =>
        new(id, null, TranscriptionStatus.Succeeded, [new(text, 0, 1000, TimingGranularity.Chunk, [])]);
    private sealed class FakeProvider(string id, Func<TranscriptionRequest, Task<TranscriptionResult>> run) : ITranscriptionProvider
    {
        public ProviderDescriptor Descriptor => new(id, id, id, true, TimingGranularity.Chunk);
        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default) => run(request);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public void Dispose() => Directory.Delete(directory, true);
}
