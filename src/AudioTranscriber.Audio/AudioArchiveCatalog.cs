using System.Security.Cryptography;
using System.Text.Json;

namespace AudioTranscriber.Audio;

public static class AudioArchiveCatalog
{
    public static ArchiveRecovery RecoverOriginals(string rootDirectory)
    {
        var chunks = new List<OriginalChunk>();
        var diagnostics = new List<string>();
        if (!Directory.Exists(rootDirectory)) return new(chunks, diagnostics);
        foreach (var track in Directory.EnumerateFiles(rootDirectory, "track.json", SearchOption.AllDirectories))
        {
            var recovery = NativeArchiveWriter.Recover(Path.GetDirectoryName(track)!);
            diagnostics.AddRange(recovery.Diagnostics);
        }
        foreach (var manifest in Directory.EnumerateFiles(rootDirectory, "*.wav.json", SearchOption.AllDirectories))
        {
            try
            {
                var chunk = JsonSerializer.Deserialize<OriginalChunk>(File.ReadAllText(manifest))
                    ?? throw new InvalidDataException("Empty native manifest.");
                if (!string.Equals(Path.GetFullPath(chunk.Path + ".json"), Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native manifest path mismatch.");
                using var file = File.OpenRead(LocalMedia.RequireFile(chunk.Path));
                if (chunk.DataBytes != checked(chunk.FrameCount * chunk.Format.BlockAlign) ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(file)), chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native archive hash or sample count mismatch.");
                chunks.Add(chunk);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or JsonException or OverflowException)
            { diagnostics.Add($"Original retained but not trusted: {manifest}: {exception.Message}"); }
        }
        return new(chunks.OrderBy(c => c.SessionStartTicks).ToArray(), diagnostics);
    }

    public static IEnumerable<NormalizedShard> ReadNormalizedManifests(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory)) yield break;
        foreach (var manifest in Directory.EnumerateFiles(rootDirectory, "*.pcm.json", SearchOption.AllDirectories))
        {
            var shard = JsonSerializer.Deserialize<NormalizedShard>(File.ReadAllText(manifest))
                ?? throw new InvalidDataException($"Invalid derivative manifest: {manifest}");
            if (!string.Equals(Path.GetFullPath(shard.Path + ".json"), Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Derivative manifest path mismatch.");
            using var file = File.OpenRead(LocalMedia.RequireFile(shard.Path));
            if (file.Length != checked(shard.SampleCount * 2) ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(file)), shard.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Derivative hash mismatch: {shard.Path}");
            yield return shard;
        }
    }
}
