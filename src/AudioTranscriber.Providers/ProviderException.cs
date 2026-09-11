using Grpc.Core;
using AudioTranscriber.Core;

namespace AudioTranscriber.Providers;

public enum ProviderFailureKind
{
    ConsentRequired, Authentication, Permission, Quota, Transient, Deadline,
    InvalidRequest, InvalidResponse, Unavailable, LocalModelMissing, Configuration
}

public sealed class ProviderException : Exception
{
    public ProviderFailureKind Kind { get; }
    public string SafeCode { get; }
    public bool IsRetryable => Kind is ProviderFailureKind.Transient or ProviderFailureKind.Deadline;
    public bool StopsEndpoint => Kind is ProviderFailureKind.Authentication or ProviderFailureKind.Permission or ProviderFailureKind.Quota;

    internal ProviderException(ProviderFailureKind kind, string safeCode)
        : base($"ASR request failed ({safeCode}).")
    {
        Kind = kind;
        SafeCode = safeCode;
    }

    internal static ProviderException FromRpc(RpcException error) => error.StatusCode switch
    {
        StatusCode.Unauthenticated => new(ProviderFailureKind.Authentication, "authentication"),
        StatusCode.PermissionDenied => new(ProviderFailureKind.Permission, "permission"),
        StatusCode.ResourceExhausted => new(ProviderFailureKind.Quota, "quota-or-rate-limit"),
        StatusCode.DeadlineExceeded => new(ProviderFailureKind.Deadline, "deadline"),
        StatusCode.Unavailable or StatusCode.Aborted => new(ProviderFailureKind.Transient, "transport-unavailable"),
        StatusCode.InvalidArgument or StatusCode.OutOfRange => new(ProviderFailureKind.InvalidRequest, "request-rejected"),
        StatusCode.NotFound or StatusCode.Unimplemented => new(ProviderFailureKind.Unavailable, "route-unavailable"),
        _ => new(ProviderFailureKind.InvalidResponse, "remote-failure")
    };

    internal TranscriptionProviderException ToContractException() => new(new(
        Kind switch
        {
            ProviderFailureKind.ConsentRequired => ProviderErrorCode.ConsentRequired,
            ProviderFailureKind.Authentication => ProviderErrorCode.Authentication,
            ProviderFailureKind.Permission => ProviderErrorCode.PermissionDenied,
            ProviderFailureKind.Quota => ProviderErrorCode.QuotaExceeded,
            ProviderFailureKind.Deadline => ProviderErrorCode.DeadlineExceeded,
            ProviderFailureKind.Transient or ProviderFailureKind.Unavailable => ProviderErrorCode.Unavailable,
            ProviderFailureKind.InvalidRequest when SafeCode == "unsupported-language" => ProviderErrorCode.UnsupportedLanguage,
            ProviderFailureKind.InvalidRequest => ProviderErrorCode.InvalidAudio,
            ProviderFailureKind.LocalModelMissing => ProviderErrorCode.ModelUnavailable,
            _ => ProviderErrorCode.InvalidResponse
        }, Message, IsRetryable));
}
