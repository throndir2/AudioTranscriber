using AudioTranscriber.Core;

namespace AudioTranscriber.Application;

public static class RecordedClockMapping
{
    public static NormalizedChunk AtNativeAnchor(NormalizedChunk normalized, NativeChunk anchor)
    {
        if (normalized.TrackId != anchor.TrackId || normalized.ContinuityId != anchor.ContinuityId ||
            normalized.SourceFormat != anchor.Format || normalized.SourceFrameOffset < anchor.SourceFrameOffset ||
            normalized.SourceFrameOffset >= anchor.SourceEndFrame)
            throw new InvalidDataException("A normalized chunk does not belong to the supplied native clock anchor.");
        return normalized with
        {
            SessionStartTicks = checked(anchor.SessionStartTicks +
                AudioTime.FramesToTicks(normalized.SourceFrameOffset - anchor.SourceFrameOffset, anchor.Format.SampleRate))
        };
    }
}
