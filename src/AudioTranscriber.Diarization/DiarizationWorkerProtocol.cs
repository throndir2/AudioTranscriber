using System.Text.Json;
using AudioTranscriber.Core;

namespace AudioTranscriber.Diarization;

public sealed record DiarizationWorkerRequest(int Version, DiarizationRequest Audio, DiarizationModelPaths Models,
    SpeakerRegistrySnapshot Registry, SpeakerMatchingOptions Matching, SpeakerEnrollment? Enrollment = null);
public sealed record DiarizationWorkerResponse(int Version, DiarizationResult Result);

public static class DiarizationWorkerProtocol
{
    public const int Version = 1;
    public const int MaximumJsonBytes = 12 * 1024 * 1024;
    public const int MaximumOutputCharacters = 65536;

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        path = LocalPaths.RequireFile(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length is <= 0 or > MaximumJsonBytes) throw new InvalidDataException("Worker JSON exceeds its size limit.");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { MaxDepth = 32 })
            ?? throw new InvalidDataException("Worker JSON is missing.");
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        path = LocalPaths.RequireDirectoryPath(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumJsonBytes) throw new InvalidDataException("Worker JSON exceeds its size limit.");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await file.WriteAsync(bytes, cancellationToken);
        await file.FlushAsync(cancellationToken);
        file.Flush(true);
    }

    public static void ValidateResult(DiarizationResult result, DiarizationRequest request, long previousRevision)
    {
        CoreRegistrySerializer.ToState(result.Registry);
        var start = checked(request.SessionStartTicks + AudioTime.FramesToTicks(request.CoreStartSample, 16000));
        var end = checked(start + AudioTime.FramesToTicks(request.CoreSampleCount ??
            request.SampleCount - request.CoreStartSample, 16000));
        if (result.Registry.SessionId != request.SessionId || result.Registry.Revision < previousRevision ||
            result.Turns.IsDefault || result.Turns.Length > 10_000 ||
            result.Diagnostics.IsDefault || result.Diagnostics.Length > 512 ||
            result.Diagnostics.Any(d => d.Length > 1024) ||
            result.Turns.Any(t => t.TrackId != request.TrackId || t.StartTicks < start || t.EndTicks > end ||
                                 t.EndTicks <= t.StartTicks || (t.Confidence is { } score && !double.IsFinite(score)) ||
                                 (t.SpeakerId is { } id && !result.Registry.Speakers.Any(e => e.Identity.Id == id))))
            throw new InvalidDataException("Worker returned invalid diarization results.");
        foreach (var turn in result.Turns)
        {
            var startSample = AudioTime.TicksToFrames(turn.StartTicks - request.SessionStartTicks, 16000);
            var endSample = AudioTime.TicksToFrames(turn.EndTicks - request.SessionStartTicks, 16000);
            if (turn.NormalizedStartSample != checked(request.NormalizedStartSample + startSample) ||
                turn.NormalizedEndSample != checked(request.NormalizedStartSample + endSample) ||
                (request.SourceSampleRate is { } rate
                    ? turn.SourceStartFrame != checked(request.SourceFrameOffset + AudioTime.Scale(startSample, rate, 16000)) ||
                      turn.SourceEndFrame != checked(request.SourceFrameOffset + AudioTime.Scale(endSample, rate, 16000))
                    : turn.SourceStartFrame is not null || turn.SourceEndFrame is not null))
                throw new InvalidDataException("Worker returned inconsistent source coordinates.");
        }
    }
}
