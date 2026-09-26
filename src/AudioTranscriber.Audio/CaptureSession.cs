using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioTranscriber.Audio;

public sealed record CaptureTrackOptions(Guid TrackId, string DeviceId, bool Loopback);
public sealed record CaptureOptions(string RootDirectory, Guid SessionId, CaptureTrackOptions Output,
    CaptureTrackOptions? Microphone = null, long QueueByteLimit = 16 * 1024 * 1024,
    long MinimumFreeBytes = 128 * 1024 * 1024, int MaxChunkSeconds = 30, long MaxChunkBytes = 64 * 1024 * 1024,
    int PauseSplitAfterMilliseconds = 0);
public sealed record TrackLevel(Guid TrackId, float Peak, float Rms);
public sealed record CapturedTrack(Guid TrackId, string Name, string DeviceId, bool IsLoopback, NativeWaveFormat Format);

/// <summary>Explicitly started selected endpoints. This class never starts recording in its constructor.</summary>
public sealed class CaptureSession : IAsyncDisposable
{
    private readonly CaptureOptions options;
    private readonly List<TrackCapture> tracks = [];
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private Timer? meter;
    public RecordingState State { get; private set; } = RecordingState.Recorded;
    public long SessionQpcOrigin100ns { get; private set; }
    public IReadOnlyList<CapturedTrack> Tracks => tracks.Where(t => t.Info is not null).Select(t => t.Info!).ToArray();
    public event Action<OriginalChunk>? ChunkSealed;
    public event Action<AudioGap>? Gap;
    public event Action<AudioFault>? Faulted;
    public event Action<TrackLevel>? LevelChanged;
    public event Action<RecordingState>? StateChanged;

    public CaptureSession(CaptureOptions options)
    {
        if (options.SessionId == Guid.Empty || options.Output.TrackId == Guid.Empty || !options.Output.Loopback ||
            options.Microphone is { Loopback: true } ||
            options.Microphone?.TrackId == options.Output.TrackId ||
            options.Microphone?.TrackId == Guid.Empty)
            throw new ArgumentException("Use distinct nonempty track IDs, a render output and an optional capture microphone.");
        this.options = options;
    }

    public static IReadOnlyList<AudioDevice> EnumerateDevices(bool loopback)
    {
        using var enumerator = new MMDeviceEnumerator();
        var collection = enumerator.EnumerateAudioEndPoints(loopback ? DataFlow.Render : DataFlow.Capture, DeviceState.Active);
        var result = new List<AudioDevice>();
        foreach (var device in collection)
        {
            using (device) result.Add(new(device.ID, device.FriendlyName, loopback));
        }
        return result;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (tracks.Count != 0) throw new InvalidOperationException("A CaptureSession may be started only once.");
            SetState(RecordingState.Starting);
            Directory.CreateDirectory(Path.GetFullPath(options.RootDirectory));
            LocalMedia.CheckSpace(options.RootDirectory, options.MinimumFreeBytes);
            SessionQpcOrigin100ns = Qpc100ns();
            var choices = options.Microphone is null ? new[] { options.Output } : new[] { options.Output, options.Microphone };
            foreach (var choice in choices)
            {
                var track = new TrackCapture(options, choice, SessionQpcOrigin100ns,
                    chunk => ChunkSealed?.Invoke(chunk), gap => Gap?.Invoke(gap), HandleFault);
                tracks.Add(track);
            }
            foreach (var track in tracks) track.Start();
            await Task.WhenAll(tracks.Select(t => t.Started)).WaitAsync(cancellationToken);
            if (State != RecordingState.Faulted) SetState(RecordingState.Recording);
            meter = new Timer(_ =>
            {
                foreach (var track in tracks) LevelChanged?.Invoke(new(track.Id, track.TakePeak(), track.TakeRms()));
            }, null, 0, 100);
        }
        catch
        {
            foreach (var track in tracks) track.RequestStop();
            await Task.WhenAll(tracks.Select(t => t.Completion));
            SetState(RecordingState.Faulted);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (tracks.Count == 0) return;
            var faulted = State == RecordingState.Faulted;
            if (!faulted) SetState(RecordingState.Stopping);
            meter?.Dispose();
            meter = null;
            foreach (var track in tracks) track.RequestStop();
            // After requesting stop, cancellation must not abandon accepted audio or truncate final packets.
            await Task.WhenAll(tracks.Select(t => t.Completion));
            if (State != RecordingState.Faulted) SetState(faulted ? RecordingState.Faulted : RecordingState.Recorded);
        }
        finally { lifecycle.Release(); }
    }

    private void HandleFault(AudioFault fault)
    {
        SetState(RecordingState.Faulted);
        foreach (var track in tracks) track.RequestStop();
        Faulted?.Invoke(fault);
    }

    private void SetState(RecordingState state) { State = state; StateChanged?.Invoke(state); }
    public static long Qpc100ns() => AudioTime.Scale(Stopwatch.GetTimestamp(), TimeSpan.TicksPerSecond, Stopwatch.Frequency);
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var track in tracks) track.Dispose();
        lifecycle.Dispose();
    }

    private sealed class TrackCapture : IDisposable
    {
        private readonly CaptureOptions options;
        private readonly CaptureTrackOptions choice;
        private readonly long origin;
        private readonly Action<OriginalChunk> sealedChunk;
        private readonly Action<AudioGap> gap;
        private readonly Action<AudioFault> fault;
        private readonly ManualResetEventSlim stop = new(false);
        private readonly PooledPacketQueue queue;
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool writerFailed;
        private bool captureFailed;
        private NativeArchiveWriter? archive;
        private long sourceFrame;
        private long acceptedThrough;
        private float peak;
        private float rms;
        public Guid Id => choice.TrackId;
        public CapturedTrack? Info { get; private set; }
        public Task Started => started.Task;
        public Task Completion => complete.Task;
        public TrackCapture(CaptureOptions options, CaptureTrackOptions choice, long origin,
            Action<OriginalChunk> sealedChunk, Action<AudioGap> gap, Action<AudioFault> fault)
        {
            this.options = options; this.choice = choice; this.origin = origin;
            this.sealedChunk = sealedChunk; this.gap = gap; this.fault = fault;
            queue = new(options.QueueByteLimit);
        }
        public void Start()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = $"WASAPI {choice.TrackId:N}" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        public void RequestStop() => stop.Set();
        public float TakePeak() => Interlocked.Exchange(ref peak, 0);
        public float TakeRms() => Interlocked.Exchange(ref rms, 0);

        private void Run()
        {
            Task? writer = null;
            AudioClient? client = null;
            AudioCaptureClient? capture = null;
            MMDevice? device = null;
            MMDeviceEnumerator? enumerator = null;
            NativeWaveFormat? format = null;
            var clientStarted = false;
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(choice.DeviceId);
                if (device.State != DeviceState.Active ||
                    device.DataFlow != (choice.Loopback ? DataFlow.Render : DataFlow.Capture))
                    throw new IOException("Selected endpoint is unavailable or has the wrong direction.");
                client = device.AudioClient;
                var native = client.MixFormat;
                format = NativeWaveFormat.FromWaveFormat(native);
                Info = new(choice.TrackId, device.FriendlyName, choice.DeviceId, choice.Loopback, format);
                archive = new(new(options.RootDirectory, options.SessionId, choice.TrackId, format, origin,
                    choice.DeviceId, choice.Loopback, options.QueueByteLimit, options.MaxChunkBytes,
                    options.MaxChunkSeconds, MinimumFreeBytes: options.MinimumFreeBytes,
                    PauseSplitAfterMilliseconds: options.PauseSplitAfterMilliseconds));
                archive.ChunkSealed += sealedChunk;
                archive.Gap += gap;
                writer = Task.Run(WritePacketsAsync);
                client.Initialize(AudioClientShareMode.Shared,
                    choice.Loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None,
                    TimeSpan.TicksPerMillisecond * 100, 0, native, Guid.Empty);
                capture = client.AudioCaptureClient;
                client.Start();
                clientStarted = true;
                started.TrySetResult();
                var lastStateCheck = Environment.TickCount64;
                while (!stop.Wait(5))
                {
                    Drain(capture, format, stopping: false);
                    if (Environment.TickCount64 - lastStateCheck >= 250)
                    {
                        if (device.State != DeviceState.Active) throw new IOException("Selected endpoint was disconnected; no default fallback was selected.");
                        lastStateCheck = Environment.TickCount64;
                    }
                }
            }
            catch (Exception exception)
            {
                captureFailed = true;
                started.TrySetException(exception);
                ReportFault(exception is OverflowException ? "QueueOverflow" : "CaptureDeviceFailure", exception.Message);
            }
            finally
            {
                try
                {
                    // IAudioClient.Stop does not consume unread packets. Drain those before closing the queue.
                    if (clientStarted)
                    {
                        client!.Stop();
                        if (capture is not null && format is not null && !writerFailed) Drain(capture, format, stopping: true);
                    }
                }
                catch (Exception exception) { captureFailed = true; ReportFault("StopDrainFailure", exception.Message); }
                queue.Complete();
                try { writer?.GetAwaiter().GetResult(); }
                catch (Exception exception) { writerFailed = true; ReportFault("ArchiveFailure", exception.Message); }
                try
                {
                    if (!writerFailed) archive?.Finish(Qpc100ns() - origin, !captureFailed);
                }
                catch (Exception exception) { ReportFault("ArchiveSealFailure", exception.Message); }
                archive?.Dispose();
                capture?.Dispose();
                client?.Dispose();
                device?.Dispose();
                enumerator?.Dispose();
                queue.Dispose();
                complete.TrySetResult();
            }
        }

        private async Task WritePacketsAsync()
        {
            try
            {
                var available = queue.WaitForDataAsync();
                while (true)
                {
                    if (await Task.WhenAny(available, Task.Delay(1000)) != available)
                    {
                        archive!.Checkpoint();
                        continue;
                    }
                    if (!await available) break;
                    while (queue.TryRead(out var packet))
                        using (packet) archive!.Write(packet!);
                    available = queue.WaitForDataAsync();
                }
            }
            catch (Exception exception)
            {
                writerFailed = true;
                stop.Set();
                ReportFault("ArchiveWriteFailure", exception.Message);
            }
        }

        private void Drain(AudioCaptureClient capture, NativeWaveFormat format, bool stopping)
        {
            while (capture.GetNextPacketSize() > 0)
            {
                if (writerFailed) throw new IOException("Archive writer failed; unread device packets were not archived.");
                var pointer = capture.GetBuffer(out var frames, out var nativeFlags, out var devicePosition, out var qpc);
                var flags = (PacketFlags)(int)nativeFlags;
                try
                {
                    var count = checked(frames * format.BlockAlign);
                    if ((flags & PacketFlags.TimestampError) != 0)
                        qpc = checked(Qpc100ns() - AudioTime.FramesToTicks(frames, format.SampleRate));
                    var startWait = Environment.TickCount64;
                    while (!queue.TryWrite(count, sourceFrame, frames, devicePosition, qpc, flags, memory =>
                    {
                        if ((flags & PacketFlags.Silent) != 0) format.FillSilence(memory.Span);
                        else
                        {
                            if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out var array))
                                throw new InvalidOperationException("Capture pool did not provide array-backed memory.");
                            Marshal.Copy(pointer, array.Array!, array.Offset, count);
                        }
                        var level = SampleMeter.Measure(memory.Span, format);
                        Interlocked.Exchange(ref peak, Math.Max(peak, level.Peak));
                        Interlocked.Exchange(ref rms, Math.Max(rms, level.Rms));
                    }))
                    {
                        if (!stopping || writerFailed || Environment.TickCount64 - startWait > 5000)
                        {
                            var start = qpc - origin;
                            sourceFrame = checked(sourceFrame + frames);
                            var diagnostic = new AudioGap(options.SessionId, choice.TrackId, start,
                                checked(start + AudioTime.FramesToTicks(frames, format.SampleRate)),
                                AudioGapKind.QueueOverflow, "Byte-bounded capture queue exhausted; this packet was not archived.");
                            var directory = Path.Combine(Path.GetFullPath(options.RootDirectory), options.SessionId.ToString("N"), choice.TrackId.ToString("N"));
                            LocalMedia.AtomicJson(Path.Combine(directory, $"gap-{Guid.NewGuid():N}.json"), diagnostic);
                            gap(diagnostic);
                            throw new OverflowException(diagnostic.Detail);
                        }
                        Thread.Sleep(2);
                    }
                    sourceFrame = checked(sourceFrame + frames);
                    Interlocked.Exchange(ref acceptedThrough, sourceFrame);
                }

                finally { capture.ReleaseBuffer(frames); }
            }
        }

        private void ReportFault(string code, string message)
        {
            var diagnostic = new AudioFault(options.SessionId, choice.TrackId, code, message,
                Interlocked.Read(ref acceptedThrough), archive?.DurableThroughFrame ?? 0);
            try
            {
                var directory = Path.Combine(Path.GetFullPath(options.RootDirectory), options.SessionId.ToString("N"), choice.TrackId.ToString("N"));
                Directory.CreateDirectory(directory);
                LocalMedia.AtomicJson(Path.Combine(directory, $"fault-{Guid.NewGuid():N}.json"), diagnostic);
            }
            catch (IOException) { /* The fault event still surfaces a full/unwritable disk. */ }
            fault(diagnostic);
        }

        public void Dispose() { stop.Dispose(); queue.Dispose(); }
    }
}
