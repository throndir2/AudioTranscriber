using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using AudioTranscriber.Core;
using Xunit;

namespace AudioTranscriber.Diarization.Tests;

public sealed class NativeRuntimeFactAttribute : FactAttribute
{
    public NativeRuntimeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIARIZATION_MODEL_DIRECTORY")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIARIZATION_WORKER_EXE")))
            Skip = "Explicitly installed models and built Windows x64 worker are required; no automatic downloads.";
    }
}

public sealed class NativeSpeechFactAttribute : FactAttribute
{
    public NativeSpeechFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIARIZATION_SYNTHETIC_PCM")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIARIZATION_MODEL_DIRECTORY")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIARIZATION_WORKER_EXE")))
            Skip = "An explicitly supplied local synthetic speech fixture and installed runtime/models are required.";
    }
}

public sealed class NativePublicSpeechFactAttribute : FactAttribute
{
    public NativePublicSpeechFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIARIZATION_PUBLIC_PCM")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIARIZATION_MODEL_DIRECTORY")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DIARIZATION_WORKER_EXE")))
            Skip = "The explicitly prepared pinned public PCM fixture and installed runtime/models are required.";
    }
}

public sealed class NativeRuntimeTests
{
    private static DiarizationModelPaths Models => DiarizationModelPaths.InDirectory(
        Environment.GetEnvironmentVariable("DIARIZATION_MODEL_DIRECTORY")!);
    private static string Worker => Environment.GetEnvironmentVariable("DIARIZATION_WORKER_EXE")!;
    private static WorkerDiarizationService CreateWorker(string directory) => new(Worker, directory, Models,
        dotnetHostPath: Environment.GetEnvironmentVariable("DIARIZATION_DOTNET_HOST"));

    [NativePublicSpeechFact]
    public async Task VerifiedPublicReadSpeechProducesRealCleanSpeakerEmbeddings()
    {
        using var directory = new TestDirectory();
        var path = Environment.GetEnvironmentVariable("DIARIZATION_PUBLIC_PCM")!;
        Assert.Equal(93680 * 2, new FileInfo(path).Length);
        await using (var file = File.OpenRead(path))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
            Assert.Equal("156e1e1821700d4c4ad31a63e6d901a5088d5ee155fafd02a9d0d2de37f771e6", hash);
        }
        var session = Guid.NewGuid();
        var request = new DiarizationRequest(session, Guid.NewGuid(), path, 93680,
            10 * TimeSpan.TicksPerSecond, SourceSampleRate: 16000);
        await using var service = CreateWorker(directory.Path);
        var result = await service.DiarizeAsync(request, new(session, 0, []));
        Assert.NotEmpty(result.Turns);
        Assert.NotEmpty(result.Registry.Speakers);
        Assert.All(result.Registry.Speakers, entry =>
        {
            Assert.Equal(256, entry.Centroid.Length);
            Assert.True(entry.EvidenceDurationTicks >= 2 * TimeSpan.TicksPerSecond);
        });
        Assert.All(result.Turns, turn => Assert.Equal(turn.NormalizedStartSample, turn.SourceStartFrame));
    }

    [NativeSpeechFact]
    public async Task ActualSyntheticSpeechExtractsEmbeddingsAndRetainsRenamedGuidAcrossCalls()
    {
        using var directory = new TestDirectory();
        var path = Environment.GetEnvironmentVariable("DIARIZATION_SYNTHETIC_PCM")!;
        var session = Guid.NewGuid();
        var request = new DiarizationRequest(session, Guid.NewGuid(), path, new FileInfo(path).Length / 2,
            7200L * TimeSpan.TicksPerSecond, NormalizedStartSample: 100_000, SourceFrameOffset: 275_625, SourceSampleRate: 44100);
        await using var service = CreateWorker(directory.Path);
        var first = await service.DiarizeAsync(request, new(session, 0, []));
        Assert.NotEmpty(first.Turns);
        Assert.NotEmpty(first.Registry.Speakers);
        Assert.All(first.Registry.Speakers, e => Assert.Equal(256, e.Centroid.Length));
        Assert.All(first.Registry.Speakers.SelectMany(e => e.Representatives), e =>
        {
            Assert.True(e.EvidenceStartTicks > request.SessionStartTicks);
            Assert.True(e.EvidenceEndTicks > e.EvidenceStartTicks);
            Assert.True(e.EvidenceEndTicks <= request.SessionStartTicks + AudioTime.FramesToTicks(request.SampleCount, 16000));
        });
        var renamed = first.Registry with
        {
            Speakers = first.Registry.Speakers.Select(e => e with
                { Identity = e.Identity with { DisplayName = $"Renamed person {e.Identity.Number}" } }).ToImmutableArray()
        };
        var restored = CoreRegistrySerializer.Deserialize(CoreRegistrySerializer.Serialize(renamed));
        var secondRequest = request with { SessionStartTicks = request.SessionStartTicks + 60 * TimeSpan.TicksPerSecond };
        var second = await service.DiarizeAsync(secondRequest, restored);
        Assert.Equal(first.Registry.Speakers.Select(e => e.Identity.Id), second.Registry.Speakers.Select(e => e.Identity.Id));
        Assert.All(second.Registry.Speakers, e => Assert.StartsWith("Renamed person", e.Identity.DisplayName));
        var mapped = DiarizationCoordinates.ToSourceIntervals(second, secondRequest);
        Assert.All(mapped, t => Assert.Equal(t.InputStartSample + 100_000, t.NormalizedSourceStartSample));
        Assert.All(mapped, t => Assert.Equal(t.NormalizedSourceStartSample, t.Turn.NormalizedStartSample));
        Assert.All(mapped, t => Assert.Equal(275_625 + AudioTime.Scale(t.InputStartSample, 44100, 16000), t.Turn.SourceStartFrame));
        Assert.All(second.Turns, t => Assert.True(t.StartTicks >= secondRequest.SessionStartTicks));
    }

    [NativeRuntimeFact]
    public async Task ActualWindowsNativeModelsProcessSilenceAndToneWithClippedSessionOffsets()
    {
        using var directory = new TestDirectory();
        var pcm = new byte[12 * 16000 * 2];
        for (var i = 3 * 16000; i < 6 * 16000; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i * 2 * Math.PI * 220 / 16000) * 1200));
        var path = Path.Combine(directory.Path, "synthetic-silence-tone.pcm");
        await File.WriteAllBytesAsync(path, pcm);
        var session = Guid.NewGuid();
        var request = new DiarizationRequest(session, Guid.NewGuid(), path, pcm.Length / 2,
            3600L * TimeSpan.TicksPerSecond, 16000, 10 * 16000);
        await using var service = CreateWorker(directory.Path);
        var result = await service.DiarizeAsync(request, new(session, 0, []));
        DiarizationWorkerProtocol.ValidateResult(result, request, 0);
        Assert.Contains(SherpaDiarizationService.AlgorithmVersion, result.Diagnostics);
        Assert.All(result.Turns, t => Assert.InRange(t.StartTicks,
            3601L * TimeSpan.TicksPerSecond, 3611L * TimeSpan.TicksPerSecond));
    }

    [NativeRuntimeFact]
    public async Task CancellationKillsOwnedWorkerAndRemovesRequestFiles()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "sixty-seconds.pcm");
        await File.WriteAllBytesAsync(path, new byte[60 * 16000 * 2]);
        using var cancellation = new CancellationTokenSource();
        await using var service = CreateWorker(directory.Path);
        int pid = 0;
        service.WorkerStarted += id =>
        {
            pid = id;
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(750));
        };
        var session = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiarizeAsync(
            new(session, Guid.NewGuid(), path, 60 * 16000, 0), new(session, 0, []), cancellation.Token));
        Assert.NotEqual(0, pid);
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
        Assert.Empty(Directory.GetDirectories(directory.Path, "diarization-*"));
    }

    [Fact]
    public void MultiHourAndWrongPcmSizeAreRejectedBeforeInference()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "tiny.pcm");
        File.WriteAllBytes(path, [0, 0]);
        var session = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => SherpaDiarizationService.ValidateRequest(
            new(session, Guid.NewGuid(), path, 3 * 3600 * 16000, 0), new(session, 0, [])));
        Assert.Throws<InvalidDataException>(() => SherpaDiarizationService.ValidateRequest(
            new(session, Guid.NewGuid(), path, 16000, 0), new(session, 0, [])));
    }
}
