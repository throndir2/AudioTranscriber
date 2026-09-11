using AudioTranscriber.Core;

namespace AudioTranscriber.Diarization;

public sealed record SpeakerTurnCoordinates(SpeakerTurn Turn, long InputStartSample, long InputEndSample,
    long NormalizedSourceStartSample, long NormalizedSourceEndSample);

public static class DiarizationCoordinates
{
    /// <summary>Maps immutable session turns to the supplied PCM and its source-normalized timeline.</summary>
    public static IReadOnlyList<SpeakerTurnCoordinates> ToSourceIntervals(DiarizationResult result,
        DiarizationRequest request, long? normalizedStartSample = null)
    {
        var sourceStart = normalizedStartSample ?? request.NormalizedStartSample;
        ArgumentOutOfRangeException.ThrowIfNegative(sourceStart);
        return result.Turns.Select(turn =>
        {
            if (turn.TrackId != request.TrackId || turn.StartTicks < request.SessionStartTicks ||
                turn.EndTicks < turn.StartTicks)
                throw new InvalidDataException("Speaker turn does not belong to this audio request.");
            var start = RoundToSample(turn.StartTicks - request.SessionStartTicks);
            var end = RoundToSample(turn.EndTicks - request.SessionStartTicks);
            if (end > request.SampleCount) throw new InvalidDataException("Speaker turn exceeds its source PCM.");
            return new SpeakerTurnCoordinates(turn, start, end,
                checked(sourceStart + start), checked(sourceStart + end));
        }).ToArray();
    }

    private static long RoundToSample(long ticks) =>
        checked((long)(((Int128)ticks * 16000 + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond));
}
