using System.Security.Cryptography;
using AudioTranscriber.Core;
using AudioTranscriber.Audio;
using Parquet;

namespace AudioTranscriber.Benchmarks;

public sealed record BenchmarkClip(string Id, string Reference, string PcmPath, long SampleCount,
    string SourceSha256, string PcmSha256, string NormalizerVersion)
{
    public double DurationSeconds => SampleCount / 16000d;
}

public static class PublicFixture
{
    public const string Dataset = "hf-internal-testing/librispeech_asr_dummy";
    public const string Revision = "5be91486e11a2d616f4ec5db8d3fd248585ac07a";
    public const string FileName = "validation-00000-of-00001.parquet";
    public const string Sha256 = "4e69a06fa5edc90921e5e7e39a7084881f8b3ed9c805c574f4f39c6fde27c603";
    public const long MaximumDownloadBytes = 15 * 1024 * 1024;
    public static Uri DownloadUri { get; } = new(
        $"https://huggingface.co/datasets/{Dataset}/resolve/{Revision}/clean/{FileName}");
    public static IReadOnlyList<string> ClipIds { get; } = Array.AsReadOnly(new[]
        { "1272-128104-0000", "1272-128104-0001" });
    public const string Attribution =
        "LibriSpeech ASR corpus, Vassil Panayotov, Guoguo Chen, Daniel Povey and Sanjeev Khudanpur (2015), " +
        "OpenSLR 12, https://www.openslr.org/12/, CC BY 4.0 https://creativecommons.org/licenses/by/4.0/. " +
        "Hugging Face hf-internal-testing dummy subset/transcoding; fixture card does not specify a license. " +
        "Two complete same-speaker clean read utterances; not a D&D or diarization benchmark.";

    public static async Task<IReadOnlyList<BenchmarkClip>> PrepareAsync(string directory,
        MediaTools? tools = null, CancellationToken cancellationToken = default)
    {
        var importer = new MediaImporter(tools);
        Directory.CreateDirectory(directory);
        var parquetPath = await DownloadAsync(directory, cancellationToken);
        var extracted = await ExtractAsync(parquetPath, directory, cancellationToken);
        var clips = new List<BenchmarkClip>();
        foreach (var id in ClipIds)
        {
            var (source, reference) = extracted[id];
            var probe = await importer.ProbeAsync(source, cancellationToken);
            if (probe.AudioStreams.Count != 1 ||
                probe.AudioStreams[0] is not { SampleRate: 16000, Channels: 1 } ||
                probe.AudioStreams[0].DurationTicks is > 150010000 ||
                probe.DurationTicks is > 150010000)
                throw new InvalidDataException("Public fixture must contain complete mono 16 kHz utterances at most 15 seconds.");
            var target = Path.Combine(directory, id + "-normalized-" + Guid.NewGuid().ToString("N"));
            var imported = await importer.ImportAsync(Guid.NewGuid(), Guid.NewGuid(),
                source, probe.AudioStreams[0].Index, Path.Combine(target, "original"), cancellationToken: cancellationToken);
            var chunks = await PersistentNormalizer.NormalizeImportAsync(imported, target, tools, cancellationToken: cancellationToken);
            if (chunks.Count != 1 || chunks[0].SampleCount is <= 0 or > 240000)
                throw new InvalidDataException("Normalization must preserve one complete utterance, never crop the reference.");
            var pcm = await Pcm16Audio.ReadAsync(chunks[0].Path, chunks[0].SampleCount, cancellationToken);
            var pcmPath = Path.Combine(directory, id + ".pcm");
            await File.WriteAllBytesAsync(pcmPath, pcm, cancellationToken);
            await using var original = File.OpenRead(source);
            clips.Add(new(id, reference, pcmPath, chunks[0].SampleCount,
                Convert.ToHexString(await SHA256.HashDataAsync(original, cancellationToken)).ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant(), probe.ProbeVersion));
        }
        return clips;
    }

    public static async Task<string> DownloadAsync(string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path)) { await VerifyAsync(path, cancellationToken); return path; }
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var response = await client.GetAsync(DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
                throw new InvalidDataException("Public fixture exceeds the 15 MiB bound.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                long total = 0;
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total = checked(total + count);
                    if (total > MaximumDownloadBytes) throw new InvalidDataException("Public fixture exceeds the 15 MiB bound.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            await VerifyAsync(partial, cancellationToken);
            File.Move(partial, path);
            return path;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static async Task VerifyAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > MaximumDownloadBytes ||
            !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pinned public fixture hash mismatch.");
    }

    private static async Task<Dictionary<string, (string Path, string Reference)>> ExtractAsync(
        string parquetPath, string directory, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string Path, string Reference)>();
        await using var reader = await ParquetReader.CreateAsync(parquetPath, cancellationToken: cancellationToken);
        if (reader.RowGroupCount > 10) throw new InvalidDataException("Unexpected public fixture row count.");
        var fields = reader.Schema.GetDataFields();
        var idField = fields.Single(f => f.Name == "id");
        var textField = fields.Single(f => f.Name == "text");
        var bytesField = fields.Single(f => f.Name == "bytes");
        for (var groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
        {
            using var group = reader.OpenRowGroupReader(groupIndex);
            if (group.RowCount > 1000) throw new InvalidDataException("Unexpected public fixture row count.");
            var ids = new string?[checked((int)group.RowCount)];
            var texts = new string?[ids.Length];
            var audio = new byte[]?[ids.Length];
            await group.ReadAsync(idField, ids.AsMemory(), cancellationToken: cancellationToken);
            await group.ReadAsync(textField, texts.AsMemory(), cancellationToken: cancellationToken);
            await group.ReadAsync(bytesField, audio.AsMemory(), cancellationToken: cancellationToken);
            for (var index = 0; index < ids.Length; index++)
            {
                var id = ids[index];
                if (id is null || !ClipIds.Contains(id)) continue;
                if (audio[index] is not byte[] bytes || bytes.Length is <= 0 or > 2 * 1024 * 1024 ||
                    texts[index] is not string reference || string.IsNullOrWhiteSpace(reference))
                    throw new InvalidDataException("Selected public utterance is incomplete.");
                var extension = bytes.AsSpan().StartsWith("fLaC"u8) ? ".flac"
                    : bytes.AsSpan().StartsWith("RIFF"u8) ? ".wav"
                    : throw new InvalidDataException("Unexpected public audio container.");
                var path = Path.Combine(directory, id + extension);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                if (!result.TryAdd(id, (path, reference)))
                    throw new InvalidDataException("Duplicate public utterance identity.");
            }
        }
        if (result.Count != ClipIds.Count) throw new InvalidDataException("Selected complete utterances are missing.");
        return result;
    }
}
