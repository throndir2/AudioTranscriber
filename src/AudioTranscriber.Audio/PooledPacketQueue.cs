using System.Buffers;
using System.Threading.Channels;

namespace AudioTranscriber.Audio;

public sealed class PooledAudioPacket : IDisposable
{
    private byte[]? buffer;
    private Action<int>? release;
    public int Length { get; }
    public long SourceFrame { get; }
    public int Frames { get; }
    public long DevicePosition { get; }
    public long Qpc100ns { get; }
    public PacketFlags Flags { get; }
    public Memory<byte> Data => (buffer ?? throw new ObjectDisposedException(nameof(PooledAudioPacket))).AsMemory(0, Length);
    internal PooledAudioPacket(byte[] buffer, int length, long sourceFrame, int frames, long devicePosition,
        long qpc100ns, PacketFlags flags, Action<int> release)
    {
        this.buffer = buffer; Length = length; SourceFrame = sourceFrame; Frames = frames;
        DevicePosition = devicePosition; Qpc100ns = qpc100ns; Flags = flags; this.release = release;
    }
    public void Dispose()
    {
        var array = Interlocked.Exchange(ref buffer, null);
        if (array is null) return;
        ArrayPool<byte>.Shared.Return(array);
        Interlocked.Exchange(ref release, null)?.Invoke(array.Length);
    }
}

/// <summary>The byte budget includes a packet currently owned by the writer and actual pool bucket sizes.</summary>
public sealed class PooledPacketQueue : IDisposable
{
    private readonly long byteLimit;
    private long reserved;
    private readonly Channel<PooledAudioPacket> channel = Channel.CreateBounded<PooledAudioPacket>(
        new BoundedChannelOptions(4096) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    public long ReservedBytes => Interlocked.Read(ref reserved);
    public PooledPacketQueue(long byteLimit)
    {
        if (byteLimit <= 0) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        this.byteLimit = byteLimit;
    }

    public bool TryWrite(int length, long sourceFrame, int frames, long devicePosition, long qpc100ns,
        PacketFlags flags, Action<Memory<byte>> copy)
    {
        if (length <= 0 || frames <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > byteLimit) return false;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        if (Interlocked.Add(ref reserved, buffer.Length) > byteLimit)
        {
            Interlocked.Add(ref reserved, -buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
            return false;
        }
        var packet = new PooledAudioPacket(buffer, length, sourceFrame, frames, devicePosition, qpc100ns, flags,
            count => Interlocked.Add(ref reserved, -count));
        try
        {
            copy(packet.Data);
            if (channel.Writer.TryWrite(packet)) return true;
            packet.Dispose();
            return false;
        }
        catch { packet.Dispose(); throw; }
    }

    public IAsyncEnumerable<PooledAudioPacket> ReadAllAsync() => channel.Reader.ReadAllAsync();
    internal Task<bool> WaitForDataAsync() => channel.Reader.WaitToReadAsync().AsTask();
    internal bool TryRead(out PooledAudioPacket? packet) => channel.Reader.TryRead(out packet);
    public void Complete() => channel.Writer.TryComplete();
    public void Dispose()
    {
        Complete();
        while (channel.Reader.TryRead(out var packet)) packet.Dispose();
    }
}
