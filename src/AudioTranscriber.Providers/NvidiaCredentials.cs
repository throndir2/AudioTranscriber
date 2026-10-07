using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber.Providers;

public sealed class NvidiaCredential : IDisposable
{
    private char[]? value;
    public NvidiaCredential(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 ||
            key.Any(c => c <= ' ' || c > '~'))
            throw new ProviderException(ProviderFailureKind.Configuration, "invalid-credential");
        value = key.ToCharArray();
    }

    internal string BearerHeader => value is { } key
        ? "Bearer " + new string(key)
        : throw new ProviderException(ProviderFailureKind.Authentication, "credential-unavailable");
    internal byte[] Encode() => value is { } key
        ? Encoding.UTF8.GetBytes(key)
        : throw new ProviderException(ProviderFailureKind.Authentication, "credential-unavailable");
    public override string ToString() => "[NVIDIA credential: redacted]";
    public void Dispose()
    {
        if (value is { } key) Array.Clear(key);
        value = null;
    }
}

public interface INvidiaCredentialSource
{
    ValueTask<NvidiaCredential?> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class NvidiaCredentialVault : INvidiaCredentialSource, IDisposable
{
    private NvidiaCredential? credential;
    private readonly object gate = new();

    public void SetMemoryOnly(string key)
    {
        var next = new NvidiaCredential(key);
        lock (gate) { credential?.Dispose(); credential = next; }
    }

    public ValueTask<NvidiaCredential?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (credential is null) return ValueTask.FromResult<NvidiaCredential?>(null);
            var bytes = credential.Encode();
            try { return ValueTask.FromResult<NvidiaCredential?>(new(Encoding.UTF8.GetString(bytes))); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    public async Task SaveForCurrentUserAsync(string path, bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyApproved)
            throw new ProviderException(ProviderFailureKind.Configuration, "credential-save-not-approved");
        using var key = await GetAsync(cancellationToken)
            ?? throw new ProviderException(ProviderFailureKind.Authentication, "credential-unavailable");
        var bytes = key.Encode();
        try
        {
            var encrypted = UserSecretProtection.Protect(bytes);
            await File.WriteAllBytesAsync(path, encrypted, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new ProviderException(ProviderFailureKind.Configuration, "credential-save-failed"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task LoadForCurrentUserAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (new FileInfo(path).Length > 16384)
                throw new ProviderException(ProviderFailureKind.Configuration, "credential-file-invalid");
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
            var bytes = UserSecretProtection.Unprotect(encrypted);
            try { SetMemoryOnly(Encoding.UTF8.GetString(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new ProviderException(ProviderFailureKind.Configuration, "credential-load-failed"); }
    }

    public void Clear() { lock (gate) { credential?.Dispose(); credential = null; } }
    public void Dispose() => Clear();
    public override string ToString() => "[NVIDIA credential vault: redacted]";
}

public static class ReadOnlyProductionCredentialLoader
{
    public const int MaximumConfigBytes = 2 * 1024 * 1024;

    public static async Task<NvidiaCredentialVault> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = null;
        try
        {
            // Open read-only; never inherit URLs, model settings, or unrelated credentials.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumConfigBytes)
                throw new ProviderException(ProviderFailureKind.Configuration, "production-config-invalid");
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var rows = new List<JsonElement>();
            FindRows(json.RootElement, rows);
            if (rows.Count != 1 || !TryProperty(rows[0], "ApiKey", out var property) ||
                property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
                throw new ProviderException(ProviderFailureKind.Configuration, "nvidia-credential-not-unique");
            var vault = new NvidiaCredentialVault();
            vault.SetMemoryOnly(property.GetString()!);
            return vault;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw new ProviderException(ProviderFailureKind.Configuration, "production-config-unreadable"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void FindRows(JsonElement element, List<JsonElement> rows)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var hasName = TryProperty(element, "ProviderName", out var name);
            var isNvidia = hasName &&
                (name.ValueKind == JsonValueKind.String && name.GetString() == "Nvidia" || IsNvidiaEnum(name));
            var hasEnum = TryProperty(element, "Provider", out var provider) && provider.ValueKind == JsonValueKind.Number;
            if ((isNvidia && hasEnum && !IsNvidiaEnum(provider)) ||
                (hasEnum && IsNvidiaEnum(provider) && hasName && !isNvidia))
                throw new ProviderException(ProviderFailureKind.Configuration, "nvidia-provider-identity-conflict");
            isNvidia |= hasEnum && IsNvidiaEnum(provider);
            if (isNvidia && TryProperty(element, "ApiKey", out var key) &&
                key.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString()))
                rows.Add(element);
            foreach (var property in element.EnumerateObject()) FindRows(property.Value, rows);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) FindRows(child, rows);
    }

    private static bool IsNvidiaEnum(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == 7;

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { value = property.Value; return true; }
        value = default;
        return false;
    }
}
