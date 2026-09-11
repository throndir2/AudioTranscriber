using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace AudioTranscriber.Audio;

[Flags]
public enum PacketFlags { None = 0, Discontinuity = 1, Silent = 2, TimestampError = 4 }
public enum AudioGapKind { TimedSilence, UnknownLoss, DeviceUnavailable, QueueOverflow, DiskFailure, InvalidTimestamp }
public enum RecordingState { Starting, Recording, Stopping, Recorded, Faulted, Recoverable }

public sealed record NativeWaveFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign,
    bool IsFloat, uint ChannelMask, int ValidBitsPerSample, byte[] SerializedFormat)
{
    public static NativeWaveFormat FromWaveFormat(WaveFormat format)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        format.Serialize(writer);
        var bytes = data.ToArray();
        var extensible = format.Encoding == WaveFormatEncoding.Extensible && bytes.Length >= 44;
        var subFormat = extensible ? new Guid(bytes.AsSpan(28, 16)) : Guid.Empty;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
            subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
        if ((!isFloat && !isPcm) || (isFloat && format.BitsPerSample is not (32 or 64)) ||
            (!isFloat && format.BitsPerSample is not (8 or 16 or 24 or 32)) ||
            format.BlockAlign != format.Channels * format.BitsPerSample / 8)
            throw new NotSupportedException("Capture requires native interleaved PCM or IEEE floating-point samples.");
        return new(format.SampleRate, format.Channels, format.BitsPerSample, format.BlockAlign, isFloat,
            extensible ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)) : 0,
            extensible ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)) : format.BitsPerSample, bytes);
    }

    public WaveFormat ToWaveFormat()
    {
        var pinned = GCHandle.Alloc(SerializedFormat, GCHandleType.Pinned);
        try { return WaveFormat.MarshalFromPtr(pinned.AddrOfPinnedObject() + 4); }
        finally { pinned.Free(); }
    }

    public string FFmpegSampleFormat => (IsFloat, BitsPerSample) switch
    {
        (true, 32) => "f32le", (true, 64) => "f64le", (false, 8) => "u8",
        (false, 16) => "s16le", (false, 24) => "s24le", (false, 32) => "s32le",
        _ => throw new NotSupportedException("Unsupported native sample representation.")
    };

    public void FillSilence(Span<byte> destination) => destination.Fill(!IsFloat && BitsPerSample == 8 ? (byte)128 : (byte)0);
}

public sealed record PacketClock(long SourceFrame, long FrameCount, long DevicePosition, long Qpc100ns, PacketFlags Flags);
public sealed record AudioGap(Guid SessionId, Guid TrackId, long StartTicks, long EndTicks, AudioGapKind Kind, string Detail);
public sealed record AudioFault(Guid SessionId, Guid TrackId, string Code, string Message,
    long AcceptedThroughFrame, long DurableThroughFrame, long? LostStartTicks = null, long? LostEndTicks = null);
public sealed record OriginalChunk(Guid Id, Guid SessionId, Guid TrackId, string Path, NativeWaveFormat Format,
    long SourceStartFrame, long FrameCount, long SessionStartTicks, long ContinuityId, long DataOffset,
    long DataBytes, string Sha256, IReadOnlyList<PacketClock> Clocks, bool Recovered = false)
{
    public long SessionEndTicks => Clocks.Count == 0
        ? checked(SessionStartTicks + AudioTime.FramesToTicks(FrameCount, Format.SampleRate))
        : checked(SessionStartTicks + Clocks[^1].Qpc100ns - Clocks[0].Qpc100ns +
            AudioTime.FramesToTicks(Clocks[^1].FrameCount, Format.SampleRate));
}
public sealed record NormalizedShard(Guid Id, Guid SessionId, Guid TrackId, string Path, long ContinuityId,
    long NormalizedStartSample, long SampleCount, long SourceStartFrame, long SourceEndFrame,
    int SourceSampleRate, long SessionStartTicks, string Sha256)
{
    public const int SampleRate = 16000;
    public long SessionEndTicks => checked(SessionStartTicks + AudioTime.FramesToTicks(SampleCount, SampleRate));
}
public sealed record AudioDevice(string Id, string Name, bool IsLoopback);

public static class AudioTime
{
    public static long Scale(long value, long numerator, long denominator)
    {
        if (denominator <= 0 || numerator < 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        return checked((long)((Int128)value * numerator / denominator));
    }
    public static long FramesToTicks(long frames, int sampleRate) => Scale(frames, TimeSpan.TicksPerSecond, sampleRate);
    public static long TicksToFrames(long ticks, int sampleRate) => Scale(ticks, sampleRate, TimeSpan.TicksPerSecond);
}

public sealed record ArchiveOptions(string RootDirectory, Guid SessionId, Guid TrackId, NativeWaveFormat Format,
    long SessionQpcOrigin100ns, string DeviceId, bool IsLoopback, long QueueByteLimit = 16 * 1024 * 1024,
    long MaxChunkBytes = 64 * 1024 * 1024, int MaxChunkSeconds = 30, int DurabilityMilliseconds = 1000,
    long MinimumFreeBytes = 128 * 1024 * 1024);
