using AudioTranscriber.Core;
using Grpc.Core;
using Grpc.Net.Client;
using Nvidia.Riva.Asr;

namespace AudioTranscriber.Providers;

/// <summary>Parakeet served by the local NIM container on 127.0.0.1. Audio never leaves this PC, so no key or consent is used.</summary>
public sealed class LocalRivaProvider : ITranscriptionProvider
{
    private readonly LocalNimProfile profile;
    private readonly IRivaTransport transport;
    // The low-memory profiles serve one request at a time.
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public ProviderDescriptor Descriptor { get; }

    public LocalRivaProvider(LocalNimProfile profile) : this(profile, new LocalRivaTransport(LocalNimHost.GrpcPort)) { }

    internal LocalRivaProvider(LocalNimProfile profile, IRivaTransport transport)
    {
        this.profile = profile;
        this.transport = transport;
        Descriptor = new(profile.Id, profile.DisplayName, profile.Image, false, TimingGranularity.Word, 30,
            DefaultLanguage: profile.Locale);
    }

    public static bool SupportsLanguage(string language) =>
        language.Equals("en", StringComparison.OrdinalIgnoreCase) || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            try { request.Validate(Descriptor.MaximumAudioSeconds); }
            catch (Exception error) when (error is ArgumentException or OverflowException)
            { throw new ProviderException(ProviderFailureKind.InvalidRequest, "invalid-audio"); }
            if (!SupportsLanguage(request.Language))
                throw new ProviderException(ProviderFailureKind.InvalidRequest, "unsupported-language");
            var pcm = await Pcm16Audio.ReadAsync(request.AudioPath, request.SampleCount, cancellationToken);
            var response = await transport.RecognizeAsync(NvidiaRivaProvider.BuildRequest(pcm, profile.Locale, true), new Metadata(),
                DateTime.UtcNow.AddSeconds(60), cancellationToken);
            return NvidiaRivaProvider.ParseResponse(profile.Id, true, request.SampleCount, response);
        }
        catch (ProviderException error) { throw error.ToContractException(); }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (RpcException error) { throw ProviderException.FromRpc(error).ToContractException(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new ProviderException(ProviderFailureKind.InvalidRequest, "audio-unreadable").ToContractException(); }
        catch (HttpRequestException)
        { throw new ProviderException(ProviderFailureKind.Transient, "local-gpu-unavailable").ToContractException(); }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed) transport.Dispose();
        disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class LocalRivaTransport : IRivaTransport
{
    private readonly GrpcChannel channel;
    private readonly RivaSpeechRecognition.RivaSpeechRecognitionClient client;

    public LocalRivaTransport(int port)
    {
        // Loopback only; NIM serves plaintext gRPC (HTTP/2 without TLS) inside the container.
        channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5) },
            DisposeHttpClient = true,
            MaxReceiveMessageSize = 4 * 1024 * 1024,
            MaxSendMessageSize = 1024 * 1024
        });
        client = new(channel);
    }

    public async Task<RecognizeResponse> RecognizeAsync(RecognizeRequest request, Metadata metadata,
        DateTime deadlineUtc, CancellationToken cancellationToken)
    {
        using var call = client.RecognizeAsync(request, metadata, deadlineUtc, cancellationToken);
        return await call.ResponseAsync.ConfigureAwait(false);
    }

    public void Dispose() => channel.Dispose();
}
