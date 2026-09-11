using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioTranscriber.Audio;

public sealed record MediaStreamInfo(int Index, string Codec, int SampleRate, int Channels,
    string SampleFormat, string ChannelLayout, long? DurationTicks, long StartTicks, string TimeBase);
public sealed record MediaProbe(string Path, long? DurationTicks, IReadOnlyList<MediaStreamInfo> AudioStreams,
    string ProbeVersion);
public sealed record ManagedImport(Guid SessionId, Guid TrackId, string ManagedPath, string SourcePath,
    string SourceName, long ByteLength, string Sha256, MediaStreamInfo Stream, DateTimeOffset ImportedAt,
    string ApplicationVersion, string ProbeVersion);
public sealed record ImportProgress(long CopiedBytes, long TotalBytes);

public sealed class MediaImporter(MediaTools? tools = null)
{
    private readonly MediaTools tools = tools ?? new();

    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        path = LocalMedia.RequireFile(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var process = new OwnedMediaProcess(tools.FFprobe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-format_whitelist", LocalMedia.Formats,
             "-show_streams", "-show_format", "-show_program_version", "-of", "json", path], timeout.Token);
        process.Input.Close();
        string output;
        try { output = await process.ReadTextAsync(1024 * 1024, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("FFprobe exceeded its 30-second deadline."); }
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        var streams = new List<MediaStreamInfo>();
        if (root.TryGetProperty("streams", out var entries))
        {
            foreach (var stream in entries.EnumerateArray())
            {
                if (Text(stream, "codec_type") != "audio") continue;
                if (!int.TryParse(Text(stream, "sample_rate"), out var rate) || rate <= 0 || rate > 768000)
                    throw new InvalidDataException("Audio stream has an invalid or unsupported sample rate.");
                var channels = stream.TryGetProperty("channels", out var c) ? c.GetInt32() : 0;
                if (channels is < 1 or > 64) throw new InvalidDataException("Audio stream has an invalid channel count.");
                streams.Add(new(stream.GetProperty("index").GetInt32(), Text(stream, "codec_name"), rate, channels,
                    Text(stream, "sample_fmt"), Text(stream, "channel_layout"), Ticks(stream, "duration"),
                    Ticks(stream, "start_time") ?? 0, Text(stream, "time_base")));
            }
        }
        if (streams.Count == 0) throw new InvalidDataException("The selected file contains no valid audio streams.");
        return new(path, root.TryGetProperty("format", out var format) ? Ticks(format, "duration") : null,
            streams, root.TryGetProperty("program_version", out var version) ? Text(version, "version") : "unknown");
    }

    public async Task<ManagedImport> ImportAsync(Guid sessionId, Guid trackId, string sourcePath,
        int streamIndex, string destinationDirectory, IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty || trackId == Guid.Empty) throw new ArgumentException("Session and track IDs are required.");
        sourcePath = LocalMedia.RequireFile(sourcePath);
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = source.Length;
        LocalMedia.CheckSpace(destinationDirectory, checked(length + 64L * 1024 * 1024));
        var name = $"{trackId:N}-{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}";
        var final = Path.Combine(destinationDirectory, name);
        var partial = final + ".partial";
        var copied = 0L;
        try
        {
            string digest;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[128 * 1024];
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        LocalMedia.CheckSpace(destinationDirectory, count + 8L * 1024 * 1024);
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        hash.AppendData(buffer, 0, count);
                        copied = checked(copied + count);
                        progress?.Report(new(copied, length));
                    }
                    await destination.FlushAsync(cancellationToken);
                    destination.Flush(flushToDisk: true);
                }
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            if (copied != length) throw new IOException("Source file changed during import.");
            // Probe the immutable candidate, not the source that could later be replaced.
            var probe = await ProbeAsync(partial, cancellationToken);
            var selected = probe.AudioStreams.SingleOrDefault(s => s.Index == streamIndex)
                ?? throw new ArgumentException("Select a valid audio stream index.", nameof(streamIndex));
            var import = new ManagedImport(sessionId, trackId, final, sourcePath, Path.GetFileName(sourcePath),
                copied, digest, selected, DateTimeOffset.UtcNow,
                typeof(MediaImporter).Assembly.GetName().Version?.ToString() ?? "unknown", probe.ProbeVersion);
            LocalMedia.AtomicJson(final + ".import-journal.json", import);
            File.Move(partial, final);
            File.SetAttributes(final, File.GetAttributes(final) | FileAttributes.ReadOnly);
            LocalMedia.AtomicJson(final + ".import.json", import);
            File.Delete(final + ".import-journal.json");
            return import;
        }
        catch
        {
            // A sealed original and journal are recoverable; a failed unsealed copy is not an import.
            if (!File.Exists(final) && !File.Exists(final + ".import-journal.json")) File.Delete(partial);
            throw;
        }
    }

    public static IReadOnlyList<ManagedImport> Recover(string directory)
    {
        var recovered = new List<ManagedImport>();
        if (!Directory.Exists(directory)) return recovered;
        foreach (var journalPath in Directory.EnumerateFiles(directory, "*.import-journal.json"))
        {
            var import = JsonSerializer.Deserialize<ManagedImport>(File.ReadAllText(journalPath))
                ?? throw new InvalidDataException($"Invalid import journal: {journalPath}");
            var final = Path.GetFullPath(import.ManagedPath);
            if (!string.Equals(Path.GetDirectoryName(final), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Import journal escapes its directory.");
            var candidate = File.Exists(final) ? final : final + ".partial";
            using (var input = File.OpenRead(candidate))
            {
                if (input.Length != import.ByteLength ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(input)), import.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Corrupt managed import retained: {candidate}");
            }
            if (candidate != final) File.Move(candidate, final);
            File.SetAttributes(final, File.GetAttributes(final) | FileAttributes.ReadOnly);
            LocalMedia.AtomicJson(final + ".import.json", import);
            File.Delete(journalPath);
            recovered.Add(import);
        }
        return recovered;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static long? Ticks(JsonElement element, string name) =>
        decimal.TryParse(Text(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? checked((long)decimal.Round(seconds * TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero)) : null;
}
