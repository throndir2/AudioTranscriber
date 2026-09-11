using System.Security.Cryptography;

namespace AudioTranscriber.Providers;

public sealed record LocalWhisperModel(string Id, string FileName, long Bytes, string Sha256)
{
    public const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    public string License => "MIT — OpenAI Whisper; GGML conversion by whisper.cpp contributors";
    public Uri DownloadUri => new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{FileName}");
    public Uri LicenseUri => new("https://github.com/openai/whisper/blob/v20250625/LICENSE");
}

public static class LocalWhisperModelCatalog
{
    public static IReadOnlyList<LocalWhisperModel> All { get; } = Array.AsReadOnly(new[]
    {
        new LocalWhisperModel("tiny", "ggml-tiny.bin", 77691713,
            "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        new LocalWhisperModel("base", "ggml-base.bin", 147951465,
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new LocalWhisperModel("small", "ggml-small.bin", 487601967,
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        new LocalWhisperModel("large-v3", "ggml-large-v3.bin", 3095033483,
            "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2")
    });
}

public static class VerifiedModelDownload
{
    public static async Task<string> InstallWhisperAsync(LocalWhisperModel model, string directory,
        bool explicitlyApproved, IProgress<long>? downloadedBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyApproved || !LocalWhisperModelCatalog.All.Contains(model))
            throw new ProviderException(ProviderFailureKind.Configuration, "model-download-not-approved");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, model.FileName);
        if (File.Exists(path))
        {
            await VerifyAsync(path, model.Bytes, model.Sha256, cancellationToken);
            await PreserveModelNoticeAsync(path, model, cancellationToken);
            return path;
        }
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
            using var response = await client.GetAsync(model.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != model.Bytes)
                throw new ProviderException(ProviderFailureKind.Configuration, "model-size-mismatch");
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total = checked(total + count);
                    if (total > model.Bytes)
                        throw new ProviderException(ProviderFailureKind.Configuration, "model-download-too-large");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    downloadedBytes?.Report(total);
                }
                await output.FlushAsync(cancellationToken);
            }
            await VerifyAsync(partial, model.Bytes, model.Sha256, cancellationToken);
            File.Move(partial, path);
            await PreserveModelNoticeAsync(path, model, cancellationToken);
            return path;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        { throw new ProviderException(ProviderFailureKind.Configuration, "model-install-failed"); }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static async Task PreserveModelNoticeAsync(string path, LocalWhisperModel model, CancellationToken cancellationToken)
    {
        await using var license = typeof(VerifiedModelDownload).Assembly.GetManifestResourceStream("WhisperModelLicense")
            ?? throw new ProviderException(ProviderFailureKind.Configuration, "model-license-unavailable");
        using var reader = new StreamReader(license);
        var text = await reader.ReadToEndAsync(cancellationToken);
        await File.WriteAllTextAsync(path + ".LICENSE.txt", text + Environment.NewLine +
            $"Model: {model.Id}; GGML conversion: whisper.cpp contributors.{Environment.NewLine}" +
            $"Source: {model.DownloadUri}{Environment.NewLine}SHA256: {model.Sha256}{Environment.NewLine}" +
            $"Exact bytes: {model.Bytes}{Environment.NewLine}", cancellationToken);
    }

    private static async Task VerifyAsync(string path, long bytes, string hash, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length != bytes ||
            !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new ProviderException(ProviderFailureKind.Configuration, "model-integrity-mismatch");
    }
}
