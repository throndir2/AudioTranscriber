using System.Collections.Immutable;
using AudioTranscriber.Core;
using Google.Protobuf;
using Grpc.Core;
using Nvidia.Riva;
using Nvidia.Riva.Asr;

namespace AudioTranscriber.Providers;

public sealed class NvidiaRivaProvider : ITranscriptionProvider
{
    public const string Authority = NvidiaRivaTransport.ApprovedAuthority;
    private static readonly SemaphoreSlim AccountGate = new(1, 1);
    private static long lastAttemptTick;
    private readonly NvidiaModel model;
    private readonly INvidiaCredentialSource credentials;
    private readonly ICloudConsentStore consent;
    private readonly IRivaTransport transport;
    private readonly TimeSpan deadline;
    private readonly bool paceRequests;
    private bool disposed;

    public ProviderDescriptor Descriptor { get; }

    public NvidiaRivaProvider(NvidiaModel model, INvidiaCredentialSource credentials,
        ICloudConsentStore consent, TimeSpan? deadline = null)
        : this(model, credentials, consent, new NvidiaRivaTransport(), deadline, true) { }

    internal NvidiaRivaProvider(NvidiaModel model, INvidiaCredentialSource credentials,
        ICloudConsentStore consent, IRivaTransport transport, TimeSpan? deadline = null, bool paceRequests = false)
    {
        NvidiaModelCatalog.RequireCatalogEntry(model);
        this.model = model;
        this.credentials = credentials;
        this.consent = consent;
        this.transport = transport;
        this.deadline = deadline ?? TimeSpan.FromSeconds(60);
        this.paceRequests = paceRequests;
        if (this.deadline < TimeSpan.FromSeconds(1) || this.deadline > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        Descriptor = new(model.Id, model.DisplayName, model.AdvertisedModel, true,
            model.VerifiedWordTiming ? TimingGranularity.Word : TimingGranularity.Chunk,
            30, model.FunctionId, model.EnglishLocale, false);
    }

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            try { request.Validate(Descriptor.MaximumAudioSeconds); }
            catch (Exception error) when (error is ArgumentException or OverflowException)
            { throw new ProviderException(ProviderFailureKind.InvalidRequest, "invalid-audio"); }
            var locale = model.GetLocale(request.Language);
            await RequireConsentAsync(request, cancellationToken);
            var pcm = await Pcm16Audio.ReadAsync(request.AudioPath, request.SampleCount, cancellationToken);
            var rpcRequest = CreateRequest(pcm, locale);
            await AccountGate.WaitAsync(cancellationToken);
            try
            {
                if (paceRequests && lastAttemptTick != 0)
                {
                    var delay = 1500 - (Environment.TickCount64 - lastAttemptTick);
                    if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
                }
                await RequireConsentAsync(request, cancellationToken);
                using var key = await credentials.GetAsync(cancellationToken)
                    ?? throw new ProviderException(ProviderFailureKind.Authentication, "credential-unavailable");
                // Re-read durable consent after waiting/potential credential IO, just before sending.
                await RequireConsentAsync(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = new Metadata
                {
                    { "function-id", model.FunctionId },
                    { "authorization", key.BearerHeader }
                };
                lastAttemptTick = Environment.TickCount64;
                var response = await transport.RecognizeAsync(rpcRequest, metadata, DateTime.UtcNow + deadline, cancellationToken);
                return ParseResponse(model, request.SampleCount, response);
            }
            finally { AccountGate.Release(); }
        }
        catch (ProviderException error) { throw error.ToContractException(); }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (RpcException error) { throw ProviderException.FromRpc(error).ToContractException(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { throw new ProviderException(ProviderFailureKind.Deadline, "deadline").ToContractException(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { throw new ProviderException(ProviderFailureKind.InvalidRequest, "audio-unreadable").ToContractException(); }
        catch (HttpRequestException)
        { throw new ProviderException(ProviderFailureKind.Transient, "transport-unavailable").ToContractException(); }
    }

    private async Task RequireConsentAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        var permission = await consent.GetAsync(request.SessionId, model.Id, cancellationToken);
        if (permission?.Allows(request.SessionId, model.Id, request.TrackId) != true ||
            string.IsNullOrWhiteSpace(permission.DisclosureVersion))
            throw new ProviderException(ProviderFailureKind.ConsentRequired, "cloud-consent-required");
    }

    internal RecognizeRequest CreateRequest(byte[] pcm, string locale) => BuildRequest(pcm, locale, model.VerifiedWordTiming);

    internal static RecognizeRequest BuildRequest(byte[] pcm, string locale, bool wordTiming) => new()
    {
        Audio = ByteString.CopyFrom(pcm),
        Config = new RecognitionConfig
        {
            Encoding = Nvidia.Riva.AudioEncoding.LinearPcm,
            SampleRateHertz = 16000,
            AudioChannelCount = 1,
            LanguageCode = locale,
            MaxAlternatives = 1,
            EnableAutomaticPunctuation = true,
            EnableWordTimeOffsets = wordTiming,
            ProfanityFilter = false,
            VerbatimTranscripts = true
            // Model stays unset: the function ID (hosted) or the single deployed profile (local) selects it.
        }
    };

    internal static TranscriptionResult ParseResponse(NvidiaModel model, long sampleCount, RecognizeResponse response) =>
        ParseResponse(model.Id, model.VerifiedWordTiming, sampleCount, response);

    internal static TranscriptionResult ParseResponse(string providerId, bool wordTiming, long sampleCount, RecognizeResponse response)
    {
        if (response.CalculateSize() > 4 * 1024 * 1024)
            throw new ProviderException(ProviderFailureKind.InvalidResponse, "response-too-large");
        var segments = ImmutableArray.CreateBuilder<TranscriptionSegment>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var durationMs = (sampleCount * 1000 + 15999) / 16000;
        var partial = false;
        var emptyResults = 0;
        var maxProcessedSeconds = 0f;
        foreach (var result in response.Results)
        {
            if (float.IsFinite(result.AudioProcessed) && result.AudioProcessed > 0)
                maxProcessedSeconds = Math.Max(maxProcessedSeconds, result.AudioProcessed);
            if (result.Alternatives.Count == 0 || string.IsNullOrWhiteSpace(result.Alternatives[0].Transcript))
            { emptyResults++; continue; }
            var alternative = result.Alternatives[0];
            var words = ImmutableArray.CreateBuilder<TranscriptionWord>();
            var invalidTimes = 0;
            if (wordTiming)
            {
                var previousStart = -1;
                foreach (var word in alternative.Words)
                {
                    if (word.StartTime < 0 || word.EndTime < word.StartTime || word.EndTime > durationMs ||
                        word.StartTime < previousStart || string.IsNullOrWhiteSpace(word.Word))
                    { invalidTimes++; continue; }
                    words.Add(new(word.Word, word.StartTime, word.EndTime, Confidence(word.Confidence)));
                    previousStart = word.StartTime;
                }
            }
            if (invalidTimes > 0) { diagnostics.Add($"invalid-word-times:{invalidTimes}"); partial = true; }
            var timing = words.Count > 0 ? TimingGranularity.Word : TimingGranularity.Chunk;
            if (words.Count == 0) diagnostics.Add("word-timing-unavailable");
            segments.Add(new(alternative.Transcript,
                words.Count > 0 ? words.Min(w => w.StartMilliseconds) : 0,
                words.Count > 0 ? words.Max(w => w.EndMilliseconds) : durationMs,
                timing, words.ToImmutable(), Confidence(alternative.Confidence)));
        }
        if (emptyResults > 0)
        {
            diagnostics.Add($"empty-results:{emptyResults}");
            partial |= segments.Count > 0;
        }
        if (maxProcessedSeconds == 0) diagnostics.Add("audio-coverage-unreported");
        else if (maxProcessedSeconds + 0.1 < sampleCount / 16000d)
        { partial = true; diagnostics.Add("audio-coverage-partial"); }
        else if (maxProcessedSeconds > sampleCount / 16000d + 0.25)
            diagnostics.Add("audio-coverage-invalid");
        // Preserve every result/alternative and raw numeric values, not request headers or server metadata.
        var safeResponse = new RecognizeResponse();
        safeResponse.Results.Add(response.Results);
        return new(providerId, null,
            segments.Count == 0 ? TranscriptionStatus.Empty : partial ? TranscriptionStatus.Partial : TranscriptionStatus.Succeeded,
            segments.ToImmutable(), JsonFormatter.Default.Format(safeResponse), diagnostics.ToImmutable());
    }

    internal static double? Confidence(float confidence) =>
        confidence == 0 || !float.IsFinite(confidence) ? null : confidence;

    public ValueTask DisposeAsync()
    {
        if (!disposed) transport.Dispose();
        disposed = true;
        return ValueTask.CompletedTask;
    }
}
