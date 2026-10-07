using System.Diagnostics;
using System.Threading.Channels;
using AudioTranscriber.Audio;
using Concentus.Structs;

namespace AudioTranscriber.Discord;

/// <summary>Who spoke when on the recorded Discord track (session ticks), named from their Discord account.</summary>
public sealed record DiscordSpeech(Guid SessionId, Guid TrackId, ulong UserId, string Name, long StartTicks, long EndTicks);

/// <summary>
/// A Discord voice channel as a recordable source. Discord sends each person's voice as its own Opus stream (48 kHz stereo,
/// 20 ms frames). Each stream is decoded and buffered briefly against network jitter; a real-time mixer adds them into one
/// 48 kHz stereo track for the session, and notes who was talking in each 20 ms so every line is named after its speaker.
/// </summary>
public sealed class DiscordVoiceRecorder : IExternalAudioSource, IDisposable
{
    private const int Rate = 48_000, Channels = 2, FrameSamples = Rate / 50, FrameValues = FrameSamples * Channels;
    private const long FrameTicks = TimeSpan.TicksPerSecond / 50;
    // Mixed audio lags real time by this much so packets that arrive a little late still make their frame.
    private const int LatencyFrames = 3;
    private const int MaxBufferedFrames = 12, TrimToFrames = 3;
    // A person's turn ends after this much silence (40 frames = 0.8 s); long turns are reported every 10 s so live lines get names.
    private const int TurnEndFrames = 40, TurnReportFrames = 500;
    // RMS of 16-bit samples above which a 20 ms frame counts as speech (about -44 dBFS).
    private const double SpeechRms = 200;

    private readonly Func<uint, ulong?> userOf;
    private readonly Func<ulong, string> nameOf;
    private readonly Lock gate = new();
    private readonly Dictionary<uint, Stream> streams = [];
    private readonly Dictionary<ulong, Turn> turns = [];
    private readonly Channel<DiscordSpeech> spoken = Channel.CreateUnbounded<DiscordSpeech>(new() { SingleReader = true });
    private readonly Task reporter;
    private Thread? mixer;
    private volatile bool running;
    private ExternalAudioStart? recording;
    private long packets, lastPacket, firstSpeaking;

    public DiscordVoiceRecorder(string name, Func<uint, ulong?> userOf, Func<ulong, string> nameOf)
    {
        Name = name;
        this.userOf = userOf;
        this.nameOf = nameOf;
        reporter = Task.Run(async () =>
        {
            await foreach (var speech in spoken.Reader.ReadAllAsync())
            {
                try { Spoke?.Invoke(speech); }
                catch (Exception error) when (error is not OutOfMemoryException) { Problem = "Naming a speaker failed: " + error.Message; }
            }
        });
    }

    public string Name { get; }
    public NativeWaveFormat Format { get; } = ExternalAudioSources.Pcm16(Rate, Channels);
    public bool Recording => running;
    public long PacketsReceived => Interlocked.Read(ref packets);
    public string? Problem { get; private set; }
    /// <summary>Raised off the mixer thread, in order, when a person's turn (or 10 s of it) is complete.</summary>
    public event Action<DiscordSpeech>? Spoke;

    /// <summary>Discord said someone is talking but no voice packet ever arrived: UDP is blocked by a firewall or NAT.</summary>
    public bool AudioBlocked
    {
        get
        {
            var first = Interlocked.Read(ref firstSpeaking);
            return first != 0 && PacketsReceived == 0 && Environment.TickCount64 - first > 6_000;
        }
    }

    /// <summary>Seconds since the last voice packet, or null before the first.</summary>
    public double? SecondsSinceAudio => PacketsReceived == 0 ? null : (Environment.TickCount64 - Interlocked.Read(ref lastPacket)) / 1000.0;

    /// <summary>Called when Discord's voice gateway reports someone started speaking.</summary>
    public void NoteSpeaking() => Interlocked.CompareExchange(ref firstSpeaking, Environment.TickCount64, 0);

    /// <summary>One received Opus packet (already decrypted). Called on NetCord's receive loop; never blocks for long.</summary>
    public void Receive(uint ssrc, ushort sequence, ReadOnlySpan<byte> opus)
    {
        Interlocked.Increment(ref packets);
        Interlocked.Exchange(ref lastPacket, Environment.TickCount64);
        lock (gate)
        {
            if (!streams.TryGetValue(ssrc, out var stream)) streams[ssrc] = stream = new(ssrc);
            if (!running) { stream.Clear(); stream.LastSequence = sequence; return; }
            if (stream.LastSequence is ushort last)
            {
                var step = (ushort)(sequence - last);
                if (step == 0 || step > 0x8000) return;
                // Conceal a few lost frames so the speaker's timing stays right; longer gaps are just silence.
                for (var lost = 1; lost < step && lost <= 5; lost++) stream.Decode([]);
            }
            stream.LastSequence = sequence;
            stream.Decode(opus);
        }
    }

    public void Start(ExternalAudioStart start)
    {
        lock (gate)
        {
            if (running) throw new InvalidOperationException("This Discord channel is already being recorded.");
            foreach (var stream in streams.Values) stream.Clear();
            turns.Clear();
            recording = start;
            running = true;
        }
        mixer = new Thread(() => Mix(start)) { IsBackground = true, Name = "Discord mixer", Priority = ThreadPriority.AboveNormal };
        mixer.Start();
    }

    public void Stop()
    {
        running = false;
        mixer?.Join();
        mixer = null;
    }

    private void Mix(ExternalAudioStart start)
    {
        var origin = CaptureSession.Qpc100ns();
        var clock = Stopwatch.StartNew();
        var sum = new int[FrameValues];
        var take = new short[FrameValues];
        var output = new byte[FrameValues * 2];
        long frame = 0;
        while (running)
        {
            var due = clock.Elapsed.Ticks / FrameTicks - LatencyFrames;
            if (frame >= due) { Thread.Sleep(5); continue; }
            Array.Clear(sum);
            lock (gate)
            {
                var now = Environment.TickCount64;
                foreach (var stream in streams.Values)
                {
                    var count = stream.Take(take, now, MaxBufferedFrames * FrameValues, TrimToFrames * FrameValues);
                    if (count == 0) continue;
                    double energy = 0;
                    for (var i = 0; i < count; i++) { sum[i] += take[i]; energy += (double)take[i] * take[i]; }
                    if (Math.Sqrt(energy / FrameValues) >= SpeechRms) Heard(stream, frame);
                }
                ReportTurns(start, origin, frame, final: false);
            }
            for (var i = 0; i < FrameValues; i++)
            {
                var value = (short)Math.Clamp(sum[i], short.MinValue, short.MaxValue);
                output[2 * i] = (byte)value;
                output[2 * i + 1] = (byte)(value >> 8);
            }
            start.Write(output, FrameSamples, origin + frame * FrameTicks);
            frame++;
        }
        lock (gate) ReportTurns(start, origin, frame, final: true);
    }

    private void Heard(Stream stream, long frame)
    {
        stream.UserId ??= userOf(stream.Ssrc);
        if (stream.UserId is not { } user) return;
        if (turns.TryGetValue(user, out var turn)) turn.LastVoice = frame;
        else turns[user] = new(frame, frame);
    }

    private void ReportTurns(ExternalAudioStart start, long origin, long frame, bool final)
    {
        foreach (var (user, turn) in turns.ToArray())
        {
            var ended = final || frame - turn.LastVoice > TurnEndFrames;
            var end = ended ? turn.LastVoice + 1 : frame;
            if (!ended && end - turn.Start < TurnReportFrames) continue;
            var startTicks = origin + turn.Start * FrameTicks - start.SessionQpcOrigin100ns;
            var endTicks = origin + end * FrameTicks - start.SessionQpcOrigin100ns;
            spoken.Writer.TryWrite(new(start.SessionId, start.TrackId, user, nameOf(user), startTicks, endTicks));
            if (ended) turns.Remove(user);
            else turn.Start = end;
        }
    }

    public void Dispose()
    {
        Stop();
        spoken.Writer.TryComplete();
        reporter.Wait(TimeSpan.FromSeconds(5));
    }

    private sealed class Turn(long start, long lastVoice)
    {
        public long Start { get; set; } = start;
        public long LastVoice { get; set; } = lastVoice;
    }

    /// <summary>One person's stream: their decoder and a small FIFO of decoded interleaved samples.</summary>
    private sealed class Stream(uint ssrc)
    {
#pragma warning disable CS0618 // Concentus' managed decoder directly: its factory may load a native opus.dll found on the PC.
        private readonly OpusDecoder decoder = new(Rate, Channels);
#pragma warning restore CS0618
        private readonly short[] decoded = new short[FrameValues * 6];
        private short[] buffer = new short[FrameValues * 16];
        private int head, count;
        private bool playing;
        private long queuedAt;
        public uint Ssrc { get; } = ssrc;
        public ulong? UserId { get; set; }
        public ushort? LastSequence { get; set; }

        public void Decode(ReadOnlySpan<byte> opus)
        {
            int samples;
            try
            {
                samples = opus.IsEmpty ? decoder.Decode([], decoded, FrameSamples, false)
                    : decoder.Decode(opus, decoded, decoded.Length / Channels, false);
            }
            catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or InvalidOperationException) { return; }
            if (count == 0) queuedAt = Environment.TickCount64;
            Append(decoded.AsSpan(0, samples * Channels));
        }

        // Up to one frame of samples; a stream starts playing once two frames are buffered or its first packet is 40 ms old.
        public int Take(short[] into, long now, int maximum, int trimTo)
        {
            if (count == 0) { playing = false; return 0; }
            if (!playing && count < FrameValues * 2 && now - queuedAt < 40) return 0;
            playing = true;
            if (count > maximum) Skip(count - trimTo);
            var taken = Math.Min(count, into.Length);
            for (var i = 0; i < taken; i++) into[i] = buffer[(head + i) % buffer.Length];
            Skip(taken);
            return taken;
        }

        public void Clear() { head = count = 0; playing = false; LastSequence = null; }

        private void Skip(int samples) { head = (head + samples) % buffer.Length; count -= samples; }

        private void Append(ReadOnlySpan<short> samples)
        {
            if (count + samples.Length > buffer.Length)
            {
                var grown = new short[Math.Max(buffer.Length * 2, count + samples.Length)];
                for (var i = 0; i < count; i++) grown[i] = buffer[(head + i) % buffer.Length];
                buffer = grown;
                head = 0;
            }
            for (var i = 0; i < samples.Length; i++) buffer[(head + count + i) % buffer.Length] = samples[i];
            count += samples.Length;
        }
    }
}
