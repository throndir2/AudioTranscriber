using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace AudioTranscriber.Diarization;

public sealed record DiarizationModelPaths(string SegmentationModelPath, string EmbeddingModelPath)
{
    public static DiarizationModelPaths InDirectory(string directory) => new(
        Path.Combine(directory, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx"),
        Path.Combine(directory, "wespeaker_en_voxceleb_resnet34_LM.onnx"));
}

public static class DiarizationModels
{
    public const string SegmentationArchiveSha256 = "24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488";
    public const string EmbeddingSha256 = "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012";
    public const long SegmentationArchiveBytes = 6_958_444;
    public const long EmbeddingBytes = 26_530_550;
    public const string SegmentationUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";
    public const string EmbeddingUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx";
    public const string SegmentationModelSha256 = "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079";
    public const long SegmentationModelBytes = 5_992_913;

    public static async Task VerifyAsync(DiarizationModelPaths paths, CancellationToken cancellationToken = default)
    {
        await VerifyFileAsync(paths.SegmentationModelPath, SegmentationModelBytes, SegmentationModelSha256, cancellationToken);
        await VerifyFileAsync(paths.EmbeddingModelPath, EmbeddingBytes, EmbeddingSha256, cancellationToken);
    }

    public static async Task VerifyFileAsync(string path, long expectedBytes, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        LocalPaths.RequireFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length != expectedBytes) throw new InvalidDataException("Local model or artifact has an unexpected size.");
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local model or artifact checksum verification failed.");
    }

    /// <summary>Explicit installation only. Call after displaying download size and model license disclosure.</summary>
    public static async Task<DiarizationModelPaths> InstallAsync(string modelDirectory,
        IProgress<long>? downloadedBytes = null, CancellationToken cancellationToken = default)
    {
        modelDirectory = LocalPaths.RequireDirectoryPath(modelDirectory);
        Directory.CreateDirectory(modelDirectory);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var archive = Path.Combine(modelDirectory, "sherpa-onnx-pyannote-segmentation-3-0.tar.bz2");
        var paths = DiarizationModelPaths.InDirectory(modelDirectory);
        await DownloadAsync(client, new Uri(SegmentationUrl), archive, SegmentationArchiveBytes,
            SegmentationArchiveSha256, downloadedBytes, cancellationToken);
        await ExtractSegmentationAsync(archive, modelDirectory, cancellationToken);
        await DownloadAsync(client, new Uri(EmbeddingUrl), paths.EmbeddingModelPath, EmbeddingBytes,
            EmbeddingSha256, downloadedBytes, cancellationToken);
        await WriteNoticesAsync(modelDirectory, cancellationToken);
        await VerifyAsync(paths, cancellationToken);
        return paths;
    }

    public static async Task DownloadAsync(HttpClient client, Uri url, string destination,
        long expectedBytes, string expectedSha256, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default, long maximumBytes = 64 * 1024 * 1024)
    {
        if (url.Scheme != Uri.UriSchemeHttps || expectedBytes <= 0 || expectedBytes > maximumBytes)
            throw new ArgumentException("Model download requires HTTPS and a bounded size.");
        destination = LocalPaths.RequireDirectoryPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            await VerifyFileAsync(destination, expectedBytes, expectedSha256, cancellationToken);
            return;
        }
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps ||
                (response.Content.Headers.ContentLength is { } size && size != expectedBytes))
                throw new InvalidDataException("Model download size or transport did not match its manifest.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    count += read;
                    if (count > expectedBytes) throw new InvalidDataException("Model download exceeded its manifest size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(count);
                }
                if (count != expectedBytes) throw new InvalidDataException("Model download was incomplete.");
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            await VerifyFileAsync(partial, expectedBytes, expectedSha256, cancellationToken);
            File.Move(partial, destination);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public static async Task ExtractSegmentationAsync(string archive, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        await VerifyFileAsync(archive, SegmentationArchiveBytes, SegmentationArchiveSha256, cancellationToken);
        destinationDirectory = LocalPaths.RequireDirectoryPath(destinationDirectory);
        var stage = Path.Combine(destinationDirectory, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            await using (var file = File.OpenRead(archive))
            using (var decompressed = new BZip2InputStream(file))
            {
                await ExtractSafeTarAsync(decompressed, stage, cancellationToken);
            }
            var model = Path.Combine(stage, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
            await VerifyFileAsync(model, SegmentationModelBytes, SegmentationModelSha256, cancellationToken);
            var license = Path.Combine(stage, "sherpa-onnx-pyannote-segmentation-3-0", "LICENSE");
            if (!File.Exists(license)) throw new InvalidDataException("Segmentation archive is missing its license.");
            var target = Path.Combine(destinationDirectory, "sherpa-onnx-pyannote-segmentation-3-0");
            Directory.CreateDirectory(target);
            File.Move(license, Path.Combine(target, "LICENSE"), true);
            File.Move(model, Path.Combine(target, "model.onnx"), true);
        }
        finally
        {
            Directory.Delete(stage, true);
        }
    }

    public static async Task ExtractSafeTarAsync(Stream uncompressedTar, string destinationDirectory,
        CancellationToken cancellationToken = default, string rootName = "sherpa-onnx-pyannote-segmentation-3-0",
        long maximumTotalBytes = 12 * 1024 * 1024)
    {
        var root = LocalPaths.RequireDirectoryPath(destinationDirectory);
        Directory.CreateDirectory(root);
        // Allow tar headers/padding on top of the declared file bytes.
        using var boundedInput = new LimitedReadStream(uncompressedTar, maximumTotalBytes + 4 * 1024 * 1024);
        using var reader = new TarReader(boundedInput, leaveOpen: true);
        long totalBytes = 0;
        var entries = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entries > 32 || entry.Length < 0 || (totalBytes += entry.Length) > maximumTotalBytes)
                throw new InvalidDataException("Model archive exceeds extraction limits.");
            var components = entry.Name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(entry.Name) || entry.Name.Contains(':') || components.Length is 0 or > 4 ||
                components.Any(c => c is "." or ".." || c.EndsWith('.') || c.EndsWith(' ')) ||
                components[0] != rootName ||
                entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Unsafe model archive entry.");
            var path = Path.GetFullPath(Path.Combine(root, Path.Combine(components)));
            var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(path))
                throw new InvalidDataException("Invalid or duplicate archive path.");
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            if (entry.DataStream is null) throw new InvalidDataException("Archive entry has no data.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536];
            long written = 0;
            int read;
            while ((read = await entry.DataStream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if ((written += read) > entry.Length) throw new InvalidDataException("Archive entry exceeded declared size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (written != entry.Length) throw new InvalidDataException("Truncated archive entry.");
        }
    }

    private static async Task WriteNoticesAsync(string directory, CancellationToken cancellationToken)
    {
        var assembly = typeof(DiarizationModels).Assembly;
        foreach (var name in new[] { "Segmentation-original-MIT.txt", "Segmentation-package-MIT.txt", "WeSpeaker-NOTICE.txt" })
        {
            await using var resource = assembly.GetManifestResourceStream("AudioTranscriber.Diarization.Licenses." + name)
                ?? throw new InvalidOperationException("Bundled model notice is missing.");
            using var reader = new StreamReader(resource, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(directory, name), await reader.ReadToEndAsync(cancellationToken), cancellationToken);
        }
    }

    private sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long count;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int length) => Count(inner.Read(buffer, offset, length));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken));
        private int Count(int read)
        {
            if ((count += read) > maximumBytes) throw new InvalidDataException("Archive decompression exceeded its limit.");
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class LocalPaths
{
    public static string RequireDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains("://", StringComparison.Ordinal) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Only local filesystem paths are accepted.");
        var full = Path.GetFullPath(path);
        if (full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':'))
            throw new ArgumentException("Alternate data streams are not accepted.");
        var current = full;
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Reparse-point paths are not accepted.");
            current = Path.GetDirectoryName(current);
        }
        return full == Path.GetPathRoot(full) ? full : full.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static string RequireFile(string path)
    {
        var full = RequireDirectoryPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("A required local file is missing.");
        return full;
    }
}
