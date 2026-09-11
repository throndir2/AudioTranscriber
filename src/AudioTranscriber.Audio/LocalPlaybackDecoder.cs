using System.Globalization;

namespace AudioTranscriber.Audio;

public static class PlaybackTimeline
{
    public static long FrameAt(OriginalChunk chunk, long sessionTicks)
    {
        if (sessionTicks <= chunk.SessionStartTicks) return 0;
        if (sessionTicks >= chunk.SessionEndTicks) return chunk.FrameCount;
        if (chunk.Clocks.Count == 0) return Math.Clamp(
            AudioTime.TicksToFrames(sessionTicks - chunk.SessionStartTicks, chunk.Format.SampleRate), 0, chunk.FrameCount);
        var qpc = checked(chunk.Clocks[0].Qpc100ns + sessionTicks - chunk.SessionStartTicks);
        var anchor = chunk.Clocks[0];
        foreach (var candidate in chunk.Clocks)
        {
            if (candidate.Qpc100ns > qpc) break;
            anchor = candidate;
        }
        return Math.Clamp(checked(anchor.SourceFrame - chunk.SourceStartFrame +
            AudioTime.TicksToFrames(qpc - anchor.Qpc100ns, chunk.Format.SampleRate)), 0, chunk.FrameCount);
    }
}

/// <summary>Opens only a local decoder, not an audio output/capture device. Output is stereo float32 LE, 48 kHz.</summary>
public static class LocalPlaybackDecoder
{
    public static OwnedMediaProcess Open(string path, int streamIndex, int sourceRate, long startFrame,
        long? endFrame = null, MediaTools? tools = null, CancellationToken cancellationToken = default)
    {
        path = LocalMedia.RequireFile(path);
        if (sourceRate <= 0 || startFrame < 0 || endFrame < startFrame || streamIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        // Seek to a whole source second before the target, then trim exact native samples.
        // This avoids decoding hours from zero without resetting the seek origin at a fractional sample.
        var seekSeconds = Math.Max(0, startFrame / sourceRate - 1);
        var seekFrames = checked(seekSeconds * sourceRate);
        var trim = $"atrim=start_sample={(startFrame - seekFrames).ToString(CultureInfo.InvariantCulture)}";
        if (endFrame is long end) trim += $":end_sample={(end - seekFrames).ToString(CultureInfo.InvariantCulture)}";
        trim += ",asetpts=PTS-STARTPTS,aresample=48000";
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "file,pipe",
            "-format_whitelist", LocalMedia.Formats
        };
        if (seekSeconds > 0) arguments.AddRange(["-ss", seekSeconds.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-i", path, "-map", $"0:{streamIndex}", "-vn", "-sn", "-dn",
            "-af", trim, "-ac", "2", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"]);
        return new((tools ?? new()).FFmpeg, arguments, cancellationToken);
    }
}
