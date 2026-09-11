using Grpc.Core;
using Grpc.Net.Client;
using Nvidia.Riva.Asr;

namespace AudioTranscriber.Providers;

internal interface IRivaTransport : IDisposable
{
    Task<RecognizeResponse> RecognizeAsync(RecognizeRequest request, Metadata metadata,
        DateTime deadlineUtc, CancellationToken cancellationToken);
}

internal sealed class NvidiaRivaTransport : IRivaTransport
{
    public const string ApprovedAuthority = "grpc.nvcf.nvidia.com:443";
    private readonly GrpcChannel channel;
    private readonly RivaSpeechRecognition.RivaSpeechRecognitionClient client;

    public NvidiaRivaTransport()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 1
        };
        channel = GrpcChannel.ForAddress("https://" + ApprovedAuthority, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            MaxReceiveMessageSize = 4 * 1024 * 1024,
            MaxSendMessageSize = 1024 * 1024,
            MaxRetryAttempts = 1,
            MaxRetryBufferSize = 0,
            MaxRetryBufferPerCallSize = 0
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
