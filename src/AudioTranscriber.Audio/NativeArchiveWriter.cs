using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber.Audio;

public sealed record ArchiveRecovery(IReadOnlyList<OriginalChunk> Chunks, IReadOnlyList<string> Diagnostics);
internal sealed record ChunkJournal(OriginalChunk Chunk, string PartialPath, long DurableBytes, bool ReadyToSeal);

/// <summary>Single-writer archive. Events are emitted only after fsync, atomic rename and manifest persistence.</summary>
public sealed class NativeArchiveWriter : IDisposable
{
    private readonly ArchiveOptions options;
    private readonly string directory;
    private readonly long chunkFrames;
    private FileStream? file;
    private string? partialPath;
    private string? journalPath;
    private OriginalChunk? current;
    private readonly List<PacketClock> clocks = [];
    private long framesWritten;
    private long continuity;
    private long? nextDeviceFrame;
    private long? nextSourceFrame;
    private long? previousEndTicks;
    private long lastCheckpoint;
    private bool finished;
    private readonly long pauseSplitFrames, pauseFrames;
    private long quietFrames;
    private bool heardInChunk;
    public long DurableThroughFrame { get; private set; }
    public event Action<OriginalChunk>? ChunkSealed;
    public event Action<AudioGap>? Gap;

    public NativeArchiveWriter(ArchiveOptions options)
    {
        if (options.SessionId == Guid.Empty || options.TrackId == Guid.Empty ||
            options.MaxChunkSeconds is < 1 or > 30 || options.MaxChunkBytes <= 0 ||
            options.MaxChunkBytes > 64L * 1024 * 1024 || options.DurabilityMilliseconds is < 1 or > 10000)
            throw new ArgumentException("Invalid archive options.", nameof(options));
        this.options = options;
        directory = Path.Combine(Path.GetFullPath(options.RootDirectory), options.SessionId.ToString("N"), options.TrackId.ToString("N"));
        Directory.CreateDirectory(directory);
        var headerBytes = checked(24 + options.Format.SerializedFormat.Length);
        chunkFrames = Math.Min(checked((long)options.Format.SampleRate * options.MaxChunkSeconds),
            (options.MaxChunkBytes - headerBytes - 1) / options.Format.BlockAlign);
        if (chunkFrames < 1) throw new ArgumentException("Chunk budget cannot fit one sample frame.");
        pauseSplitFrames = (long)options.Format.SampleRate * Math.Max(0, options.PauseSplitAfterMilliseconds) / 1000;
        pauseFrames = (long)options.Format.SampleRate * Math.Max(1, options.PauseMilliseconds) / 1000;
        LocalMedia.CheckSpace(directory, options.MinimumFreeBytes);
        LocalMedia.AtomicJson(Path.Combine(directory, "track.json"), options);
    }

    public void Write(PooledAudioPacket packet)
    {
        if (finished) throw new InvalidOperationException("Archive is finished.");
        if (packet.Length != checked(packet.Frames * options.Format.BlockAlign))
            throw new InvalidDataException("Incomplete native sample frame.");
        if (nextSourceFrame is long expected && packet.SourceFrame < expected)
            throw new InvalidDataException("Overlapping or reordered source frames cannot be archived as contiguous audio.");
        if ((packet.Flags & PacketFlags.Silent) != 0) options.Format.FillSilence(packet.Data.Span);
        var ticks = checked(packet.Qpc100ns - options.SessionQpcOrigin100ns);
        if ((packet.Flags & PacketFlags.TimestampError) != 0)
        {
            Seal();
            continuity++;
            PublishGap(new(options.SessionId, options.TrackId, previousEndTicks ?? ticks, ticks,
                AudioGapKind.InvalidTimestamp, "WASAPI timestamp-error flag; clock position is approximate."));
        }
        else if (previousEndTicks is long previous)
        {
            var delta = ticks - previous;
            var discontinuity = (packet.Flags & PacketFlags.Discontinuity) != 0 || nextSourceFrame != packet.SourceFrame;
            var deviceJump = nextDeviceFrame != packet.DevicePosition;
            if (discontinuity || deviceJump || Math.Abs(delta) > TimeSpan.TicksPerMillisecond * 20)
            {
                Seal();
                continuity++;
                var silence = options.IsLoopback && !discontinuity && delta > 0;
                PublishGap(new(options.SessionId, options.TrackId, previous, Math.Max(previous, ticks),
                    silence ? AudioGapKind.TimedSilence : AudioGapKind.UnknownLoss,
                    silence ? "Healthy loopback produced no packets in this timed interval."
                            : $"Capture continuity changed (device jump={deviceJump}, flags={packet.Flags}, clock delta={delta})."));
            }
        }
        else if (ticks > TimeSpan.TicksPerMillisecond * 20)
        {
            PublishGap(new(options.SessionId, options.TrackId, 0, ticks,
                options.IsLoopback ? AudioGapKind.TimedSilence : AudioGapKind.UnknownLoss,
                options.IsLoopback ? "No initial loopback packets." : "Microphone startup interval."));
        }
        var pauseSplit = pauseSplitFrames > 0;
        var quiet = pauseSplit && SampleMeter.Measure(packet.Data.Span, options.Format).Rms < options.PauseRms;
        // Speech starting after a quiet stretch begins a fresh chunk instead of being cut by the length cap.
        if (pauseSplit && !quiet && current is not null && !heardInChunk && framesWritten >= pauseSplitFrames) Seal();
        var consumed = 0;
        while (consumed < packet.Frames)
        {
            if (current is null) Open(packet, consumed);
            var count = (int)Math.Min(packet.Frames - consumed, chunkFrames - framesWritten);
            LocalMedia.CheckSpace(directory, checked(options.MinimumFreeBytes + (long)count * options.Format.BlockAlign));
            file!.Write(packet.Data.Span.Slice(consumed * options.Format.BlockAlign, count * options.Format.BlockAlign));
            clocks.Add(new(checked(packet.SourceFrame + consumed), count, checked(packet.DevicePosition + consumed),
                checked(packet.Qpc100ns + AudioTime.FramesToTicks(consumed, options.Format.SampleRate)), packet.Flags));
            framesWritten = checked(framesWritten + count);
            consumed += count;
            if (!quiet) heardInChunk = true;
            if (framesWritten == chunkFrames) Seal();
            else if (Environment.TickCount64 - lastCheckpoint >= options.DurabilityMilliseconds) Checkpoint();
        }
        if (pauseSplit)
        {
            quietFrames = quiet ? quietFrames + packet.Frames : 0;
            // Seal at the end of a pause so the phrase just spoken can be recognized immediately.
            if (current is not null && heardInChunk && quietFrames >= pauseFrames && framesWritten >= pauseSplitFrames) Seal();
        }
        previousEndTicks = checked(ticks + AudioTime.FramesToTicks(packet.Frames, options.Format.SampleRate));
        nextDeviceFrame = checked(packet.DevicePosition + packet.Frames);
        nextSourceFrame = checked(packet.SourceFrame + packet.Frames);
    }

    public void Finish(long? stopSessionTicks = null, bool healthyLoopback = true)
    {
        if (finished) return;
        Seal();
        finished = true;
        if (stopSessionTicks is long stop && stop > (previousEndTicks ?? 0) + TimeSpan.TicksPerMillisecond * 20)
            PublishGap(new(options.SessionId, options.TrackId, previousEndTicks ?? 0, stop,
                options.IsLoopback && healthyLoopback ? AudioGapKind.TimedSilence : AudioGapKind.UnknownLoss,
                options.IsLoopback && healthyLoopback ? "No loopback packets before stop." : "Capture unavailable before stop."));
        LocalMedia.AtomicJson(Path.Combine(directory, "recorded.json"),
            new { options.SessionId, options.TrackId, DurableThroughFrame, StopTicks = stopSessionTicks, CompletedAt = DateTimeOffset.UtcNow });
    }

    private void Open(PooledAudioPacket packet, int offset)
    {
        var id = Guid.NewGuid();
        var final = Path.Combine(directory, $"{id:N}.wav");
        partialPath = final + ".partial";
        journalPath = final + ".journal.json";
        file = new FileStream(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536);
        using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(0u); writer.Write("WAVEfmt "u8);
            writer.Write(options.Format.SerializedFormat);
            writer.Write("data"u8); writer.Write(0u);
        }
        framesWritten = 0;
        clocks.Clear();
        current = new(id, options.SessionId, options.TrackId, final, options.Format,
            checked(packet.SourceFrame + offset), 0,
            checked(packet.Qpc100ns - options.SessionQpcOrigin100ns + AudioTime.FramesToTicks(offset, options.Format.SampleRate)),
            continuity, file.Position, 0, "", []);
        Checkpoint();
    }

    private OriginalChunk Snapshot() => current! with
    {
        FrameCount = framesWritten, DataBytes = checked(framesWritten * options.Format.BlockAlign), Clocks = clocks.ToArray()
    };

    public void Checkpoint()
    {
        if (file is null) return;
        file.Flush(flushToDisk: true);
        var snapshot = Snapshot();
        LocalMedia.AtomicJson(journalPath!, new ChunkJournal(snapshot, partialPath!, snapshot.DataBytes, false));
        DurableThroughFrame = checked(snapshot.SourceStartFrame + snapshot.FrameCount);
        lastCheckpoint = Environment.TickCount64;
    }

    private void Seal()
    {
        if (file is null) return;
        var snapshot = Snapshot();
        RepairHeader(file, snapshot.DataOffset, snapshot.DataBytes);
        file.Flush(flushToDisk: true);
        file.Position = 0;
        snapshot = snapshot with { Sha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() };
        LocalMedia.AtomicJson(journalPath!, new ChunkJournal(snapshot, partialPath!, snapshot.DataBytes, true));
        file.Dispose();
        file = null;
        File.Move(partialPath!, snapshot.Path);
        LocalMedia.AtomicJson(snapshot.Path + ".json", snapshot);
        File.Delete(journalPath!);
        DurableThroughFrame = checked(snapshot.SourceStartFrame + snapshot.FrameCount);
        current = null;
        heardInChunk = false;
        quietFrames = 0;
        ChunkSealed?.Invoke(snapshot);
    }

    private void PublishGap(AudioGap gap)
    {
        LocalMedia.AtomicJson(Path.Combine(directory, $"gap-{Guid.NewGuid():N}.json"), gap);
        Gap?.Invoke(gap);
    }

    private static void RepairHeader(FileStream stream, long offset, long bytes)
    {
        var end = checked(offset + bytes);
        stream.SetLength(end + (bytes & 1));
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 4;
        writer.Write(checked((uint)(stream.Length - 8)));
        stream.Position = offset - 4;
        writer.Write(checked((uint)bytes));
        stream.Position = stream.Length;
    }

    public static ArchiveRecovery Recover(string trackDirectory)
    {
        var recovered = new List<OriginalChunk>();
        var diagnostics = new List<string>();
        if (!Directory.Exists(trackDirectory)) return new(recovered, diagnostics);
        var directory = Path.GetFullPath(trackDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var journalPath in Directory.EnumerateFiles(directory, "*.wav.journal.json"))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<ChunkJournal>(File.ReadAllText(journalPath))
                    ?? throw new InvalidDataException("Empty chunk journal.");
                var chunk = journal.Chunk;
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(chunk.Path)), directory, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(journal.PartialPath, chunk.Path + ".partial", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Chunk journal escapes its track directory.");
                if (File.Exists(chunk.Path))
                {
                    using var existing = File.OpenRead(chunk.Path);
                    if (!journal.ReadyToSeal || !string.Equals(Convert.ToHexString(SHA256.HashData(existing)),
                        chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Sealed chunk hash mismatch.");
                }
                else
                {
                    var expectedHash = chunk.Sha256;
                    using var partial = new FileStream(journal.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var available = Math.Max(0, partial.Length - chunk.DataOffset);
                    var bytes = Math.Min(available, journal.DurableBytes);
                    bytes -= bytes % chunk.Format.BlockAlign;
                    if (bytes != journal.DurableBytes)
                        diagnostics.Add($"Truncated durable data in {journal.PartialPath}; retained original evidence.");
                    var staging = chunk.Path + ".recovered";
                    using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        var buffer = new byte[65536];
                        var remaining = checked(chunk.DataOffset + bytes);
                        while (remaining > 0)
                        {
                            var count = partial.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (count == 0) throw new EndOfStreamException("Missing native WAV header.");
                            output.Write(buffer, 0, count);
                            remaining -= count;
                        }
                        RepairHeader(output, chunk.DataOffset, bytes);
                        output.Flush(flushToDisk: true);
                        output.Position = 0;
                        var frames = bytes / chunk.Format.BlockAlign;
                        chunk = chunk with { DataBytes = bytes, FrameCount = frames, Recovered = true,
                            Clocks = chunk.Clocks.Where(c => c.SourceFrame < chunk.SourceStartFrame + frames)
                                .Select(c => c with { FrameCount = Math.Min(c.FrameCount, chunk.SourceStartFrame + frames - c.SourceFrame) }).ToArray(),
                            Sha256 = Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant() };
                        if (journal.ReadyToSeal && !string.Equals(expectedHash, chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Sealed partial hash mismatch; corrupt evidence retained.");
                    }
                    File.Move(staging, chunk.Path);
                    diagnostics.Add($"Recovered checkpointed frames from {journal.PartialPath}; partial retained as evidence.");
                }
                LocalMedia.AtomicJson(chunk.Path + ".json", chunk);
                File.Delete(journalPath);
                recovered.Add(chunk);
            }
            catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or OverflowException)
            { diagnostics.Add($"Recovery requires attention: {journalPath}: {exception.Message}"); }
        }
        foreach (var wav in Directory.EnumerateFiles(directory, "*.wav"))
            if (!File.Exists(wav + ".json") && !File.Exists(wav + ".journal.json"))
                diagnostics.Add($"Unindexed original retained (no trustworthy clock journal): {wav}");
        foreach (var partial in Directory.EnumerateFiles(directory, "*.wav.partial"))
        {
            var original = partial[..^".partial".Length];
            if (!File.Exists(original + ".journal.json") && !File.Exists(original + ".json"))
                diagnostics.Add($"Unjournaled partial retained for inspection: {partial}");
        }
        return new(recovered, diagnostics);
    }

    public void Dispose()
    {
        // Disposal after a write failure deliberately leaves the partial and last durable journal.
        file?.Dispose();
        file = null;
    }
}
