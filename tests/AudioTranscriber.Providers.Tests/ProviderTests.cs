using System.Collections.Immutable;
using AudioTranscriber.Core;
using AudioTranscriber.Providers;
using Grpc.Core;
using Nvidia.Riva.Asr;
using Xunit;

namespace AudioTranscriber.Providers.Tests;

public sealed class ProviderTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, "provider-test-data", Guid.NewGuid().ToString("N"));
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly Guid trackId = Guid.NewGuid();
    public ProviderTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task RequestUsesRealPcmExactRouteLocaleAndNoModelOrTranslation()
    {
        var request = await RequestAsync(16000);
        var fake = new FakeTransport { Response = Response("test", 1, (0, 0, "test", 0)) };
        using var vault = new NvidiaCredentialVault();
        vault.SetMemoryOnly("public-test-placeholder");
        await using var provider = new NvidiaRivaProvider(NvidiaModelCatalog.Parakeet, vault, Consent(), fake);
        var result = await provider.TranscribeAsync(request);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(await File.ReadAllBytesAsync(request.AudioPath), fake.Request!.Audio.ToByteArray());
        var config = fake.Request.Config;
        Assert.Equal(Nvidia.Riva.AudioEncoding.LinearPcm, config.Encoding);
        Assert.Equal(16000, config.SampleRateHertz);
        Assert.Equal(1, config.AudioChannelCount);
        Assert.Equal("en-GB", config.LanguageCode);
        Assert.Equal("", config.Model);
        Assert.Null(config.DiarizationConfig);
        Assert.Empty(config.SpeechContexts);
        Assert.Empty(config.CustomConfiguration);
        Assert.True(config.EnableWordTimeOffsets);
        Assert.True(config.EnableAutomaticPunctuation);
        Assert.True(config.VerbatimTranscripts);
        Assert.Equal(1, config.MaxAlternatives);
        Assert.Equal("2b940e91-a70e-4483-a958-d50408b96589", fake.Headers!.GetValue("function-id"));
        Assert.Equal("Bearer public-test-placeholder", fake.Headers.GetValue("authorization"));
        Assert.Equal(2, fake.Headers.Count);
        Assert.InRange(fake.Deadline, DateTime.UtcNow.AddSeconds(50), DateTime.UtcNow.AddSeconds(65));
        Assert.Equal(TranscriptionStatus.Succeeded, result.Status);
    }

    [Fact]
    public void ParsesEveryResultAlternativeAndIntegerMillisecondZeroDuration()
    {
        var response = Response("go go", 1, (123, 123, "go", 0), (234, 340, "go", -0.3f));
        response.Results[0].Alternatives.Add(new SpeechRecognitionAlternative { Transcript = "alternate hypothesis" });
        response.Results.Add(Response("again", 1, (400, 450, "again", 0)).Results[0]);
        var result = NvidiaRivaProvider.ParseResponse(NvidiaModelCatalog.Parakeet, 16000, response);
        Assert.Equal(2, result.Segments.Length);
        Assert.Equal(123, result.Segments[0].Words[0].StartMilliseconds);
        Assert.Equal(123, result.Segments[0].Words[0].EndMilliseconds);
        Assert.Null(result.Segments[0].Words[0].Confidence);
        Assert.Null(result.Segments[0].Confidence);
        Assert.Equal(-0.3f, result.Segments[0].Words[1].Confidence);
        Assert.Equal("go", result.Segments[0].Words[1].Text);
        Assert.Contains("alternate hypothesis", result.RawResponse!);
        Assert.Null(result.ObservedModel);
    }

    [Theory]
    [InlineData("nvidia-canary", "en-US")]
    [InlineData("nvidia-whisper-large-v3", "en")]
    public void TextOnlyRoutesNeverInventWordTimingOrDiarization(string modelId, string locale)
    {
        var model = NvidiaModelCatalog.Get(modelId);
        Assert.Equal(locale, model.GetLocale("en"));
        Assert.False(model.NativeDiarization);
        var result = NvidiaRivaProvider.ParseResponse(model, 16000, Response("hello there", 1));
        var segment = Assert.Single(result.Segments);
        Assert.Equal(TimingGranularity.Chunk, segment.Timing);
        Assert.Empty(segment.Words);
        Assert.Equal(0, segment.StartMilliseconds);
        Assert.Equal(1000, segment.EndMilliseconds);
        Assert.Contains("word-timing-unavailable", result.Diagnostics);
    }

    [Fact]
    public void InvalidTimesRetainRawEvidenceAndMarkPartial()
    {
        var result = NvidiaRivaProvider.ParseResponse(NvidiaModelCatalog.Parakeet, 16000,
            Response("good bad", 1, (0, 100, "good", 0), (-5, 30, "bad", 0)));
        Assert.Equal(TranscriptionStatus.Partial, result.Status);
        Assert.Single(result.Segments[0].Words);
        Assert.Contains("invalid-word-times:1", result.Diagnostics);
        Assert.Contains("-5", result.RawResponse!);
    }

    [Fact]
    public void DistinguishesEmptyPartialCoverageAndUnreportedCoverage()
    {
        Assert.Equal(TranscriptionStatus.Empty,
            NvidiaRivaProvider.ParseResponse(NvidiaModelCatalog.Canary, 16000, new()).Status);
        Assert.Equal(TranscriptionStatus.Partial,
            NvidiaRivaProvider.ParseResponse(NvidiaModelCatalog.Canary, 16000, Response("tail omitted", .5f)).Status);
        var unknown = NvidiaRivaProvider.ParseResponse(NvidiaModelCatalog.Canary, 16000, Response("text", 0));
        Assert.Equal(TranscriptionStatus.Succeeded, unknown.Status);
        Assert.Contains("audio-coverage-unreported", unknown.Diagnostics);
    }

    [Theory]
    [InlineData(ConsentState.NotGranted)]
    [InlineData(ConsentState.Revoked)]
    public async Task ConsentRejectsBeforeCredentialsAndNetwork(ConsentState state)
    {
        var credentials = new CountingCredentials();
        var transport = new FakeTransport();
        await using var provider = new NvidiaRivaProvider(NvidiaModelCatalog.Parakeet, credentials, Consent(state), transport);
        var error = await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(Request()));
        Assert.Equal(ProviderErrorCode.ConsentRequired, error.Error.Code);
        Assert.Equal(0, credentials.Reads);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task RevocationDuringCredentialAcquisitionBlocksSend()
    {
        var request = await RequestAsync(1);
        var consent = Consent();
        var credentials = new CountingCredentials(() => consent.State = ConsentState.Revoked);
        var transport = new FakeTransport();
        await using var provider = new NvidiaRivaProvider(NvidiaModelCatalog.Parakeet, credentials, consent, transport);
        var error = await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(request));
        Assert.Equal(ProviderErrorCode.ConsentRequired, error.Error.Code);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task ConsentIsScopedToTrackSessionAndProvider()
    {
        var transport = new FakeTransport();
        var consent = Consent();
        consent.TrackId = Guid.NewGuid();
        await using var provider = new NvidiaRivaProvider(NvidiaModelCatalog.Parakeet, new CountingCredentials(), consent, transport);
        var error = await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(Request()));
        Assert.Equal(ProviderErrorCode.ConsentRequired, error.Error.Code);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated, ProviderErrorCode.Authentication, false)]
    [InlineData(StatusCode.PermissionDenied, ProviderErrorCode.PermissionDenied, false)]
    [InlineData(StatusCode.ResourceExhausted, ProviderErrorCode.QuotaExceeded, false)]
    [InlineData(StatusCode.Unavailable, ProviderErrorCode.Unavailable, true)]
    [InlineData(StatusCode.DeadlineExceeded, ProviderErrorCode.DeadlineExceeded, true)]
    [InlineData(StatusCode.InvalidArgument, ProviderErrorCode.InvalidAudio, false)]
    public async Task ErrorsNeverContainRemoteBodyMetadataOrCredentials(StatusCode status, ProviderErrorCode code, bool transient)
    {
        var transport = new FakeTransport
        {
            Error = new RpcException(new Status(status, "REMOTE BODY public-test-placeholder"),
                new Metadata { { "debug", "PRIVATE CONFIG" } })
        };
        await using var provider = new NvidiaRivaProvider(NvidiaModelCatalog.Parakeet, new CountingCredentials(), Consent(), transport);
        var request = await RequestAsync(1);
        var error = await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(request));
        Assert.Equal(code, error.Error.Code);
        Assert.Equal(transient, error.Error.IsTransient);
        Assert.DoesNotContain("REMOTE", error.ToString());
        Assert.DoesNotContain("placeholder", error.ToString());
        Assert.DoesNotContain("PRIVATE", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task RejectsOversizedOrMislabeledPcmWithoutSending()
    {
        var transport = new FakeTransport();
        await using var provider = new NvidiaRivaProvider(NvidiaModelCatalog.Parakeet, new CountingCredentials(), Consent(), transport);
        await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(Request() with { SampleCount = 480001 }));
        var request = await RequestAsync(1);
        await File.AppendAllTextAsync(request.AudioPath, "mismatched PCM");
        await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task UnknownLocaleAndUnapprovedRouteAreRejected()
    {
        Assert.Throws<ProviderException>(() => new NvidiaRivaProvider(
            NvidiaModelCatalog.Parakeet with { FunctionId = "arbitrary-route" }, new CountingCredentials(), Consent(), new FakeTransport()));
        await using var provider = new NvidiaRivaProvider(NvidiaModelCatalog.Parakeet, new CountingCredentials(), Consent(), new FakeTransport());
        var error = await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(Request() with { Language = "xx" }));
        Assert.Equal(ProviderErrorCode.UnsupportedLanguage, error.Error.Code);
        Assert.Equal("grpc.nvcf.nvidia.com:443", NvidiaRivaProvider.Authority);
    }

    [Fact]
    public async Task LocalWhisperIsLazyAndNeverFallsBackWhenModelMissing()
    {
        await using var provider = new LocalWhisperProvider(Path.Combine(directory, "not-installed.bin"));
        Assert.False(provider.Descriptor.IsCloud);
        var error = await Assert.ThrowsAsync<TranscriptionProviderException>(() => provider.TranscribeAsync(Request()));
        Assert.Equal(ProviderErrorCode.ModelUnavailable, error.Error.Code);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void LocalWhisperConfidenceAveragesOnlyTextTokens()
    {
        var tokens = new[]
        {
            new Whisper.net.WhisperToken { Text = "[_BEG_]", Probability = 0.01f },
            new Whisper.net.WhisperToken { Text = " hello", Probability = 0.9f },
            new Whisper.net.WhisperToken { Text = " there", Probability = 0.5f },
            new Whisper.net.WhisperToken { Text = "[_TT_150]", Probability = 0.02f },
        };
        var (mean, lowest, count) = LocalWhisperProvider.SegmentConfidence(tokens);
        Assert.Equal(0.7, mean!.Value, 3);
        Assert.Equal(0.5, lowest!.Value, 3);
        Assert.Equal(2, count);
        Assert.Equal((null, null, 0), LocalWhisperProvider.SegmentConfidence([new Whisper.net.WhisperToken { Text = "[_EOT_]", Probability = 1 }]));
    }

    private TranscriptionRequest Request() => new(sessionId, trackId, Guid.NewGuid(), Path.Combine(directory, "audio.pcm"), 1, "en", 0);
    private async Task<TranscriptionRequest> RequestAsync(int samples)
    {
        var request = Request() with { SampleCount = samples };
        await File.WriteAllBytesAsync(request.AudioPath, new byte[samples * 2]);
        return request;
    }
    private ConsentStub Consent(ConsentState state = ConsentState.Granted) => new(sessionId, trackId, state);
    internal static RecognizeResponse Response(string text, float processed, params (int Start, int End, string Text, float Confidence)[] words)
    {
        var alternative = new SpeechRecognitionAlternative { Transcript = text };
        alternative.Words.Add(words.Select(w => new WordInfo { Word = w.Text, StartTime = w.Start, EndTime = w.End, Confidence = w.Confidence }));
        var result = new SpeechRecognitionResult { AudioProcessed = processed };
        result.Alternatives.Add(alternative);
        var response = new RecognizeResponse();
        response.Results.Add(result);
        return response;
    }

    private sealed class ConsentStub(Guid sessionId, Guid trackId, ConsentState state) : ICloudConsentStore
    {
        public ConsentState State { get; set; } = state;
        public Guid TrackId { get; set; } = trackId;
        public Task<CloudConsent?> GetAsync(Guid session, string provider, CancellationToken ct = default) =>
            Task.FromResult<CloudConsent?>(new(sessionId, provider, [TrackId], State, DateTimeOffset.UtcNow, "test-disclosure"));
    }
    private sealed class CountingCredentials(Action? onRead = null) : INvidiaCredentialSource
    {
        public int Reads { get; private set; }
        public ValueTask<NvidiaCredential?> GetAsync(CancellationToken ct = default)
        { Reads++; onRead?.Invoke(); return ValueTask.FromResult<NvidiaCredential?>(new("public-test-placeholder")); }
    }
    private sealed class FakeTransport : IRivaTransport
    {
        public RecognizeResponse Response { get; init; } = new();
        public Exception? Error { get; init; }
        public int Calls { get; private set; }
        public RecognizeRequest? Request { get; private set; }
        public Metadata? Headers { get; private set; }
        public DateTime Deadline { get; private set; }
        public Task<RecognizeResponse> RecognizeAsync(RecognizeRequest request, Metadata headers, DateTime deadline, CancellationToken ct)
        {
            Calls++; Request = request; Headers = headers; Deadline = deadline;
            return Error is null ? Task.FromResult(Response) : Task.FromException<RecognizeResponse>(Error);
        }
        public void Dispose() { }
    }
    public void Dispose() => Directory.Delete(directory, true);
}
