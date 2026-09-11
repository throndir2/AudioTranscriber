namespace AudioTranscriber.Core;

public static class AudioTime
{
    public const long TicksPerSecond = 10_000_000;
    public const int NormalizedSampleRate = 16_000;

    // Int128 intermediates prevent overflow for multi-hour recordings, even
    // where multiplying a valid Int64 position before division would overflow.
    public static long Scale(long value, long numerator, long denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        ArgumentOutOfRangeException.ThrowIfNegative(numerator);
        return checked((long)((Int128)value * numerator / denominator));
    }

    public static long FramesToTicks(long frames, int sampleRate) => Scale(frames, TicksPerSecond, sampleRate);
    public static long TicksToFrames(long ticks, int sampleRate) => Scale(ticks, sampleRate, TicksPerSecond);
    public static long MillisecondsToTicks(long milliseconds) => checked(milliseconds * 10_000);
    public static long RelativeMillisecondsToSessionTicks(long sessionStartTicks, long milliseconds) =>
        checked(sessionStartTicks + MillisecondsToTicks(milliseconds));
    public static long SourceToNormalized(long sourceFrames, int sourceSampleRate) =>
        Scale(sourceFrames, NormalizedSampleRate, sourceSampleRate);
}

public sealed record AudioTimeMapping(
    Guid ContinuityId, long SourceStartFrame, int SourceSampleRate,
    long NormalizedStartSample, long SessionStartTicks)
{
    public long SourceFrameToSessionTicks(long sourceFrame) =>
        checked(SessionStartTicks + AudioTime.FramesToTicks(checked(sourceFrame - SourceStartFrame), SourceSampleRate));
    public long NormalizedSampleToSessionTicks(long normalizedSample) =>
        checked(SessionStartTicks + AudioTime.FramesToTicks(checked(normalizedSample - NormalizedStartSample), 16_000));
    public long NormalizedSampleToSourceFrame(long normalizedSample) =>
        checked(SourceStartFrame + AudioTime.Scale(checked(normalizedSample - NormalizedStartSample), SourceSampleRate, 16_000));
}
