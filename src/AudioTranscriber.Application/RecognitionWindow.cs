using AudioTranscriber.Storage;
using System.Text.Json;
using AudioTranscriber.Core;

namespace AudioTranscriber.Application;

public sealed record RecognitionWindow(
    string Path, int SampleCount, long SessionStartTicks, int CoreStartSample, int CoreSampleCount,
    bool IncludeCoreEnd = false)
{
    public long CoreStartTicks => checked(SessionStartTicks + CoreStartSample * TimeSpan.TicksPerSecond / 16000L);
    public long CoreEndTicks => checked(CoreStartTicks + CoreSampleCount * TimeSpan.TicksPerSecond / 16000L);
}

public static class RecognitionWindowBuilder
{
    public static async Task<RecognitionWindow> CreateAsync(
        StoredAudioChunk current, StoredAudioChunk? previous, StoredAudioChunk? next,
        bool includeContext, string destination, CancellationToken cancellationToken = default)
    {
        const int contextSamples = 3 * 16000;
        if (current.SampleCount is <= 0 or > 30 * 16000)
            throw new InvalidDataException("Recognition cores must contain between one sample and 30 seconds.");
        var available = 30 * 16000 - current.SampleCount;
        var before = includeContext && previous is not null && Adjacent(previous, current)
            ? Math.Min(Math.Min(contextSamples, available / 2), previous.SampleCount) : 0;
        var after = includeContext && next is not null && Adjacent(current, next)
            ? Math.Min(Math.Min(contextSamples, available - before), next.SampleCount) : 0;
        var temporary = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             65_536, FileOptions.Asynchronous))
            {
                if (before > 0) await CopyAsync(previous!, previous!.SampleCount - before, before, output, cancellationToken);
                await CopyAsync(current, 0, current.SampleCount, output, cancellationToken);
                if (after > 0) await CopyAsync(next!, 0, after, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new(destination, before + current.SampleCount + after,
            checked(current.StartTicks - before * TimeSpan.TicksPerSecond / 16000L), before, current.SampleCount,
            next is null || !Adjacent(current, next));
    }

    internal static bool Adjacent(StoredAudioChunk first, StoredAudioChunk second)
    {
        // QPC anchors can differ slightly from nominal sample time; a different continuity is still never bridged.
        if (first.TrackId != second.TrackId || checked(first.StartSample + first.SampleCount) != second.StartSample ||
            Math.Abs(checked(first.StartTicks + first.SampleCount * TimeSpan.TicksPerSecond / 16000L - second.StartTicks)) >
            10 * TimeSpan.TicksPerMillisecond)
            return false;
        var left = JsonSerializer.Deserialize<NormalizedChunk>(first.MetadataJson);
        var right = JsonSerializer.Deserialize<NormalizedChunk>(second.MetadataJson);
        return left is not null && right is not null && left.ContinuityId == right.ContinuityId && left.SourceFormat == right.SourceFormat;
    }

    private static async Task CopyAsync(StoredAudioChunk chunk, int offset, int samples, Stream output, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(chunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65_536, FileOptions.Asynchronous);
        if (input.Length != chunk.SampleCount * 2L)
            throw new InvalidDataException("A normalized chunk does not match its committed PCM16 sample count.");
        input.Position = offset * 2L;
        var buffer = new byte[Math.Min(65_536, samples * 2)];
        var remaining = samples * 2;
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0) throw new EndOfStreamException("A normalized chunk ended before its committed sample count.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }
}
