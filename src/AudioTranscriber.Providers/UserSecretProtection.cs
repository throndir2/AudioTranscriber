using System.Security.Cryptography;

namespace AudioTranscriber.Providers;

/// <summary>
/// Protects small secrets for the current OS user: Windows DPAPI, or on Linux AES-GCM with a random per-user key
/// kept in ~/.config/AudioTranscriber/secret.key (mode 0600, readable only by this account).
/// </summary>
public static class UserSecretProtection
{
    private static readonly byte[] Magic = "ATK1"u8.ToArray();
    private static readonly object Gate = new();

    public static string Description => OperatingSystem.IsWindows() ? "encrypted for your Windows account" : "encrypted with a key only your Linux account can read";

    public static byte[] Protect(byte[] plain)
    {
        if (OperatingSystem.IsWindows()) return ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    public static byte[] Unprotect(byte[] protectedBytes)
    {
        if (OperatingSystem.IsWindows()) return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        if (protectedBytes.Length < 32 || !protectedBytes.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Unrecognized protected secret.");
        var plain = new byte[protectedBytes.Length - 32];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(protectedBytes.AsSpan(4, 12), protectedBytes.AsSpan(32), protectedBytes.AsSpan(16, 16), plain);
        return plain;
    }

    private static byte[] Key()
    {
        lock (Gate)
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var directory = Path.Combine(config, "AudioTranscriber");
            var path = Path.Combine(directory, "secret.key");
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 32) return existing;
                throw new CryptographicException("The per-user secret key file is damaged.");
            }
            Directory.CreateDirectory(directory);
            var key = RandomNumberGenerator.GetBytes(32);
            using (var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            })) stream.Write(key);
            return key;
        }
    }
}
