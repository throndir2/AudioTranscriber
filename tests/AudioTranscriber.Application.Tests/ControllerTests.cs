using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AudioTranscriber.Application;
using AudioTranscriber.Core;
using AudioTranscriber.Diarization;
using AudioTranscriber.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AudioTranscriber.Application.Tests;

public sealed class ControllerTests
{
    [Fact]
    public async Task StopSealsTailWithoutCancelingSlowRecognition()
    {
        await using var fixture = new Fixture();
        var session = await fixture.App.StartRecordingAsync("Synthetic capture", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        Assert.False(fixture.App.IsRecording);
        Assert.Equal(38000, fixture.App.Store.GetChunks(fixture.Capture.TrackId).Sum(chunk => chunk.SampleCount));
        Assert.Equal("Recorded", fixture.App.Store.GetSession(session.Id).State);
        await UntilAsync(() => fixture.Provider.Calls > 0);
        Assert.False(fixture.Provider.WasCanceled);
        fixture.Provider.Release.TrySetResult();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 3);
        var transcript = fixture.App.Store.GetTranscriptPage(session.Id);
        Assert.Equal(2, transcript.Count);
        Assert.All(transcript, row => Assert.Equal("Speaker 1", row.SpeakerName));
        Assert.Equal(21_000_000, transcript[1].StartTicks);
    }

    [Fact]
    public async Task CloudConsentGatesEveryQueuedSessionAndNeverImplicitlyOptsIn()
    {
        await using var fixture = new Fixture("nvidia-parakeet-tdt-v3");
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Public synthetic consent test", "synthetic-output", null,
            "nvidia-parakeet-tdt-v3", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 1);
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.False(fixture.App.Store.GetSession(session.Id).CloudConsent);
        fixture.App.SetCloudConsent(session.Id, true);
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 3);
        Assert.Equal(2, fixture.Provider.Calls);
    }

    [Fact]
    public async Task PauseRejectsLateResultsAndResumeReusesDurableChunks()
    {
        await using var fixture = new Fixture();
        var session = await fixture.App.StartRecordingAsync("Synthetic pause", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.Provider.Calls > 0);
        fixture.App.PauseTranscription(session.Id);
        await UntilAsync(() => fixture.Provider.WasCanceled);
        Assert.Empty(fixture.App.Store.GetTranscriptPage(session.Id));
        fixture.Provider.Release.TrySetResult();
        fixture.App.ResumeTranscription(session.Id);
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 3);
        Assert.Equal(2, fixture.App.Store.GetTranscriptPage(session.Id).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspensionIncludesChunksSealedAfterPauseOrCancel(bool canceled)
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Synthetic future chunks", "synthetic-output", null,
            "local-whisper", "en", false);
        if (canceled) fixture.App.CancelTranscription(session.Id);
        else fixture.App.PauseTranscription(session.Id);
        fixture.Capture.Emit(32000);
        fixture.Capture.Emit(16000);
        await fixture.App.StopRecordingAsync();
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.All(fixture.App.Store.GetJobs(session.Id), job => Assert.Equal(canceled ? "Canceled" : "Paused", job.State));
        Assert.Equal(canceled ? "Canceled" : "Paused", fixture.App.Store.GetSession(session.Id).ProcessingState);
        fixture.App.ResumeTranscription(session.Id);
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 4);
        Assert.Equal(3, fixture.Provider.Calls);
    }

    [Fact]
    public async Task InvalidRetainedAudioFailsOnlyAffectedJobs()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        fixture.Media.CorruptOutput = true;
        var bad = await fixture.App.StartRecordingAsync("Synthetic invalid bytes", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(bad.Id).Failed == 3);
        fixture.Media.CorruptOutput = false;
        var good = await fixture.App.StartRecordingAsync("Synthetic valid bytes", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(good.Id).Succeeded == 3);
    }

    [Fact]
    public async Task FailedInitialMediaCheckpointDoesNotLeaveAStaleRegistration()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var input = Path.Combine(fixture.Root, "synthetic.pcm");
        await File.WriteAllBytesAsync(input, new byte[64000]);
        fixture.Sql("""
            CREATE TRIGGER reject_media_start BEFORE UPDATE OF state ON sessions WHEN new.state='Importing'
            BEGIN SELECT RAISE(ABORT,'Synthetic one-time checkpoint failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => fixture.App.ImportAudioAsync(
            "Synthetic checkpoint recovery", input, 0, "local-whisper", "en", false));
        var session = Assert.Single(fixture.App.Store.GetSessions());
        fixture.Sql("DROP TRIGGER reject_media_start");
        fixture.App.ResumeTranscription(session.Id);
        await UntilAsync(() => fixture.App.Store.GetSession(session.Id).State == "Recorded");
        Assert.Equal(1, fixture.Media.CopyCalls);
    }

    [Fact]
    public async Task ResumeRecoversSealedImportWithoutTheExternalSource()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var session = fixture.App.Store.CreateSession("Synthetic orphan recovery", "local-whisper", "en");
        var trackId = Guid.NewGuid();
        var directory = Path.Combine(session.Directory, "imports");
        Directory.CreateDirectory(directory);
        var retained = Path.Combine(directory, "retained.pcm");
        var bytes = new byte[64000];
        await File.WriteAllBytesAsync(retained, bytes);
        var source = Path.Combine(fixture.Root, "no-longer-present.pcm");
        var manifest = new AudioTranscriber.Audio.ManagedImport(session.Id, trackId, retained, source, "source.pcm",
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            new(0, "pcm_s16le", 16000, 1, "s16", "mono", TimeSpan.FromSeconds(2).Ticks, 0, "1/16000"),
            DateTimeOffset.UtcNow, "synthetic-test", "synthetic-probe");
        await File.WriteAllTextAsync(retained + ".import.json", JsonSerializer.Serialize(manifest));
        fixture.App.Store.AddTrack(new(trackId, session.Id, "Imported", "Synthetic import", null, 0,
            JsonSerializer.Serialize(new { SourcePath = source, StreamIndex = 0, Imported = (object?)null })));
        fixture.App.Store.SetSessionState(session.Id, "Recoverable");
        fixture.App.ResumeTranscription(session.Id);
        await UntilAsync(() => fixture.App.Store.GetSession(session.Id).State == "Recorded");
        Assert.Equal(0, fixture.Media.CopyCalls);
        Assert.Equal(retained, Assert.Single(fixture.App.Store.GetTracks(session.Id)).OriginalPath);
    }

    [Fact]
    public async Task TransientLeaseWriteFailureDoesNotTerminateTheScheduler()
    {
        await using var fixture = new Fixture();
        var session = await fixture.App.StartRecordingAsync("Synthetic lease recovery", "synthetic-output", null,
            "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.Provider.Calls > 0);
        fixture.Sql("""
            CREATE TRIGGER reject_renewal BEFORE UPDATE OF lease_until ON jobs
            WHEN old.state='Running' AND new.state='Running'
            BEGIN SELECT RAISE(ABORT,'Synthetic transient lease failure'); END;
            """);
        await UntilAsync(() => fixture.Notifications.Any(item => item.Message.StartsWith("Lease renewal is temporarily", StringComparison.Ordinal)), 45);
        fixture.Sql("DROP TRIGGER reject_renewal");
        fixture.Provider.Release.TrySetResult();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 3);
    }

    [Fact]
    public async Task ContinueRecordingAppendsANewPartAfterTheExistingAudio()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Synthetic continued", "synthetic-output", null, "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 3);
        var firstEnd = fixture.App.Store.GetSession(session.Id).DurationTicks;

        var continued = await fixture.App.ContinueRecordingAsync(session.Id, "synthetic-output", null);
        Assert.Equal(session.Id, continued.Id);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id).Succeeded == 6);

        Assert.Single(fixture.App.Store.GetSessions());
        var tracks = fixture.App.Store.GetTracks(session.Id);
        Assert.Equal(["Windows output", "Windows output (part 2)"], tracks.Select(track => track.Name));
        var part2 = fixture.App.Store.GetChunks(tracks[1].Id);
        Assert.Equal(firstEnd + 2 * TimeSpan.TicksPerSecond, part2[0].StartTicks);
        var transcript = fixture.App.Store.GetTranscriptPage(session.Id);
        Assert.Equal(4, transcript.Count);
        Assert.Equal([tracks[0].Id, tracks[0].Id, tracks[1].Id, tracks[1].Id], transcript.Select(row => row.TrackId));
        Assert.True(transcript[2].StartTicks > firstEnd);
        Assert.Equal("Recorded", fixture.App.Store.GetSession(session.Id).State);
        Assert.True(fixture.App.Store.GetSession(session.Id).DurationTicks >= part2[^1].StartTicks);
    }

    [Fact]
    public async Task MergeStitchesLaterSessionsAfterTheEarliestAndDeletesAllTheirFolders()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var first = await fixture.App.StartRecordingAsync("Synthetic part one", "synthetic-output", null, "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(first.Id).Succeeded == 3);
        await Task.Delay(20);
        var second = await fixture.App.StartRecordingAsync("Synthetic part two", "synthetic-output", null, "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(second.Id).Succeeded == 3);
        var firstEnd = fixture.App.Store.GetSession(first.Id).DurationTicks;
        var secondRows = fixture.App.Store.GetTranscriptPage(second.Id);

        var merged = await fixture.App.MergeSessionsAsync([second.Id, first.Id]);

        Assert.Equal(first.Id, merged.Id);
        Assert.Equal("Synthetic part one", Assert.Single(fixture.App.Store.GetSessions()).Name);
        Assert.Equal(6, fixture.App.Store.GetProgress(first.Id).Succeeded);
        var tracks = fixture.App.Store.GetTracks(first.Id);
        Assert.Equal(["Windows output", "Windows output (part 2)"], tracks.Select(track => track.Name));
        var transcript = fixture.App.Store.GetTranscriptPage(first.Id);
        Assert.Equal(4, transcript.Count);
        var offset = firstEnd + 2 * TimeSpan.TicksPerSecond;
        Assert.Equal(secondRows.Select(row => row.StartTicks + offset), transcript.Skip(2).Select(row => row.StartTicks));
        Assert.Equal(["Speaker 1", "Speaker 1", "Speaker 2", "Speaker 2"], transcript.Select(row => row.SpeakerName));
        Assert.Equal(2, fixture.App.Store.GetSpeakers(first.Id).Count);
        var registry = CoreRegistrySerializer.Deserialize(fixture.App.Store.GetSpeakerRegistry(first.Id)!);
        Assert.Equal([1, 2], registry.Speakers.Select(entry => entry.Identity.Number));
        var chunk = fixture.App.Store.GetChunks(tracks[1].Id)[0];
        Assert.Equal(chunk.StartTicks, JsonSerializer.Deserialize<NormalizedChunk>(chunk.MetadataJson)!.SessionStartTicks);
        Assert.Equal([second.Directory], fixture.App.Store.GetMergedFolders(first.Id));

        await fixture.App.DeleteSessionsAsync([first.Id]);
        Assert.False(Directory.Exists(first.Directory));
        Assert.False(Directory.Exists(second.Directory));
    }

    [Fact]
    public async Task FillSpeakersMatchesUnlabeledLinesToTheVoicesTheUserLabeled()
    {
        await using var fixture = new Fixture();
        fixture.Provider.SegmentMilliseconds = 1500;
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Synthetic voices", "synthetic-output", null, "local-whisper", "en", false);
        float[] Voice(double amplitude) => Enumerable.Range(0, 32000).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 300 * i / 16000.0))).ToArray();
        foreach (var loud in new[] { true, false, true, false, true, false }) fixture.Capture.EmitAudio(fixture.Capture.TrackId, Voice(loud ? 0.5 : 0.05));
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id) is { Pending: 0, Running: 0, Succeeded: > 0 }, 30);
        var rows = fixture.App.Store.GetTranscriptPage(session.Id);
        var progress = fixture.App.Store.GetProgress(session.Id);
        Assert.True(rows.Count >= 6, $"{rows.Count} rows; {progress}; " + string.Join(" | ", fixture.App.Store.GetJobs(session.Id).Select(job => job.State + " " + job.Error)));
        var alice = fixture.App.GetOrCreateSpeaker(session.Id, "Alice");
        var bob = fixture.App.GetOrCreateSpeaker(session.Id, "Bob");
        fixture.App.AssignSpeaker(session.Id, [rows[0].Id], alice.Id);
        fixture.App.AssignSpeaker(session.Id, [rows[1].Id], bob.Id);

        var summary = await fixture.App.FillSpeakersFromLabelsAsync(session.Id);

        var filled = fixture.App.Store.GetTranscriptPage(session.Id);
        Assert.Equal(["Alice", "Bob", "Alice", "Bob", "Alice", "Bob"], filled.Take(6).Select(row => row.SpeakerName));
        Assert.All(filled.Skip(2).Take(4), row => Assert.True(row.VoiceFilled && !row.ManualSpeaker));
        Assert.True(summary.Filled >= 4);
        // Background speaker analysis must not undo the fill.
        fixture.App.Store.ApplyAutomaticSpeakerAssignments([(filled[2].Id, null, true)]);
        Assert.Equal("Alice", fixture.App.Store.GetTranscriptPage(session.Id)[2].SpeakerName);
    }

    [Fact]
    public async Task FillBridgesAnUnclearLineBetweenTheSameSpeakerSoThePassageJoins()
    {
        await using var fixture = new Fixture();
        fixture.Provider.SegmentMilliseconds = 1500;
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Synthetic bridge", "synthetic-output", null, "local-whisper", "en", false);
        float[] Voice(double amplitude) => Enumerable.Range(0, 32000).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 300 * i / 16000.0))).ToArray();
        foreach (var amplitude in new[] { 0.5, 0.2, 0.5, 0.05 }) fixture.Capture.EmitAudio(fixture.Capture.TrackId, Voice(amplitude));
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetProgress(session.Id) is { Pending: 0, Running: 0, Succeeded: > 0 }, 30);
        var rows = fixture.App.Store.GetTranscriptPage(session.Id);
        Assert.True(rows.Count >= 4);
        fixture.App.AssignSpeaker(session.Id, [rows[0].Id], fixture.App.GetOrCreateSpeaker(session.Id, "Alice").Id);
        fixture.App.AssignSpeaker(session.Id, [rows[3].Id], fixture.App.GetOrCreateSpeaker(session.Id, "Bob").Id);

        await fixture.App.FillSpeakersFromLabelsAsync(session.Id);

        var filled = fixture.App.Store.GetTranscriptPage(session.Id);
        Assert.Equal(["Alice", "Alice", "Alice", "Bob"], filled.Take(4).Select(row => row.SpeakerName));
        var lines = TranscriptLine.Group(filled).ToList();
        Assert.Equal([rows[0].Id, rows[1].Id, rows[2].Id], lines[0].Rows.Select(row => row.Id));
    }

    [Fact]
    public async Task NamingTheMicrophoneLabelsItsExistingAndLaterLines()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Synthetic mic name", "synthetic-output", "synthetic-mic", "local-whisper", "en", false, reduceEcho: false);
        float[] Voice() => Enumerable.Range(0, 32000).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 300 * i / 16000.0))).ToArray();
        var mic = fixture.Capture.MicrophoneTrackId;
        fixture.Capture.EmitAudio(mic, Voice());
        await UntilAsync(() => fixture.App.Store.GetTranscriptPage(session.Id).Any(row => row.TrackId == mic));
        Assert.Equal(LibraryStore.MicrophoneDefaultName, fixture.App.Store.GetTranscriptPage(session.Id).First(row => row.TrackId == mic).SpeakerName);

        Assert.Equal(1, fixture.App.SetMicrophoneSpeaker(session.Id, "Mozar"));
        fixture.Capture.EmitAudio(mic, Voice());
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetTranscriptPage(session.Id).Count(row => row.TrackId == mic) >= 2);

        var micRows = fixture.App.Store.GetTranscriptPage(session.Id).Where(row => row.TrackId == mic).ToArray();
        Assert.All(micRows, row => Assert.True(row.SpeakerName == "Mozar" && row.ManualSpeaker));
        Assert.All(fixture.App.Store.GetTranscriptPage(session.Id).Where(row => row.TrackId != mic), row => Assert.NotEqual("Mozar", row.SpeakerName));
        Assert.Equal(fixture.App.Store.GetSpeakers(session.Id).Single(speaker => speaker.Name == "Mozar").Id,
            fixture.App.Store.GetSession(session.Id).MicrophoneSpeakerId);
    }

    [Fact]
    public async Task MicrophoneWaitsForSpeakerAudioAndHasItsEchoRemoved()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        var session = await fixture.App.StartRecordingAsync("Synthetic echo", "synthetic-output", "synthetic-mic",
            "local-whisper", "en", false);
        var random = new Random(5);
        var speaker = new float[10 * 16000];
        for (var i = 0; i < speaker.Length; i++)
            speaker[i] = (float)((random.NextDouble() * 2 - 1) * 0.3 * Math.Max(0, Math.Sin(2 * Math.PI * 3.1 * i / 16000.0)));
        var microphone = new float[speaker.Length];
        for (var i = 1300; i < microphone.Length; i++) microphone[i] = speaker[i - 1280] * 0.4f + speaker[i - 1300] * 0.2f;
        var micTrack = fixture.Capture.MicrophoneTrackId;
        fixture.Capture.EmitAudio(micTrack, microphone);
        await UntilAsync(() => fixture.App.Store.GetJobs(session.Id).Any(job =>
            job.TrackId == micTrack && job.Error?.StartsWith("Waiting for the matching speaker audio", StringComparison.Ordinal) == true));
        fixture.Capture.EmitAudio(fixture.Capture.TrackId, speaker);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.Provider.TailDecibels.ContainsKey(micTrack), 30);
        double raw = 0;
        for (var i = 3 * 16000; i < microphone.Length; i++) raw += microphone[i] * microphone[i];
        var rawDecibels = 10 * Math.Log10(raw / (microphone.Length - 3 * 16000));
        Assert.True(rawDecibels - fixture.Provider.TailDecibels[micTrack] > 20,
            $"Echo fell only from {rawDecibels:F1} dB to {fixture.Provider.TailDecibels[micTrack]:F1} dB.");
        await UntilAsync(() => fixture.App.Store.GetTranscriptPage(session.Id).Any(row => row.TrackId == micTrack));
        Assert.Contains(fixture.App.Store.GetTranscriptPage(session.Id), row =>
            row.TrackId == micTrack && row.Provenance.Contains("speaker echo removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NamedVoiceIsRecognizedInALaterSessionUntilForgotten()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        async Task<StoredSession> RecordAsync(string name)
        {
            var session = await fixture.App.StartRecordingAsync(name, "synthetic-output", null, "local-whisper", "en", false);
            fixture.Capture.Emit(32000);
            await fixture.App.StopRecordingAsync();
            await UntilAsync(() => fixture.App.Store.GetSpeakers(session.Id).Count == 1 &&
                fixture.App.Store.GetProgress(session.Id) is { Succeeded: 3, Pending: 0, Running: 0 });
            return session;
        }

        var first = await RecordAsync("First synthetic session");
        var speaker = Assert.Single(fixture.App.Store.GetSpeakers(first.Id));
        Assert.Equal("Speaker 1", speaker.Name);
        await fixture.App.RenameSpeakerAsync(first.Id, speaker.Id, "Alice");
        var voice = Assert.Single(fixture.App.GetVoiceLibrary());
        Assert.Equal("Alice", voice.Name);
        Assert.All(voice.Samples, sample => Assert.Equal(first.Id, sample.SessionId));

        var second = await RecordAsync("Second synthetic session");
        var recognized = Assert.Single(fixture.App.Store.GetSpeakers(second.Id));
        Assert.Equal("Alice", recognized.Name);
        Assert.Equal("voice:" + voice.Id.ToString("D"), recognized.ParticipantId);
        Assert.All(fixture.App.Store.GetTranscriptPage(second.Id), row => Assert.Equal("Alice", row.SpeakerName));
        // Automatic recognition never feeds the library; only names the user gives do.
        Assert.All(Assert.Single(fixture.App.GetVoiceLibrary()).Samples, sample => Assert.Equal(first.Id, sample.SessionId));

        // Renaming a remembered voice's speaker to someone else withdraws that session's samples.
        await fixture.App.RenameSpeakerAsync(first.Id, speaker.Id, "Bob");
        Assert.Equal("Bob", Assert.Single(fixture.App.GetVoiceLibrary()).Name);

        fixture.App.ForgetAllVoices();
        Assert.Empty(fixture.App.GetVoiceLibrary());
        Assert.All(fixture.App.Store.GetSpeakers(second.Id).Concat(fixture.App.Store.GetSpeakers(first.Id)),
            item => Assert.Null(item.ParticipantId));
        var third = await RecordAsync("Third synthetic session");
        Assert.Equal("Speaker 1", Assert.Single(fixture.App.Store.GetSpeakers(third.Id)).Name);
    }

    [Fact]
    public async Task SpeakersNamedBeforeTheVoiceLibraryAreLearnedOnce()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Release.TrySetResult();
        fixture.App.SetRememberVoices(false);
        var session = await fixture.App.StartRecordingAsync("Older synthetic session", "synthetic-output", null, "local-whisper", "en", false);
        fixture.Capture.Emit(32000);
        await fixture.App.StopRecordingAsync();
        await UntilAsync(() => fixture.App.Store.GetSpeakers(session.Id).Count == 1 &&
            fixture.App.Store.GetProgress(session.Id) is { Succeeded: 3, Pending: 0, Running: 0 });
        var speaker = Assert.Single(fixture.App.Store.GetSpeakers(session.Id));
        await fixture.App.RenameSpeakerAsync(session.Id, speaker.Id, "Alice");
        Assert.Empty(fixture.App.GetVoiceLibrary());

        // The first-run backfill waits for remembering to be on.
        Assert.Empty(await fixture.App.RememberAllSessionVoicesAsync(firstRunOnly: true));
        fixture.App.SetRememberVoices(true);
        Assert.Equal(["Alice"], await fixture.App.RememberAllSessionVoicesAsync(firstRunOnly: true));
        var voice = Assert.Single(fixture.App.GetVoiceLibrary());
        Assert.Equal("Alice", voice.Name);
        Assert.All(voice.Samples, sample => Assert.Equal(session.Id, sample.SessionId));

        fixture.App.ForgetAllVoices();
        Assert.Empty(await fixture.App.RememberAllSessionVoicesAsync(firstRunOnly: true));
        Assert.Empty(fixture.App.GetVoiceLibrary());
        Assert.Equal(["Alice"], await fixture.App.RememberAllSessionVoicesAsync());
        Assert.Single(fixture.App.GetVoiceLibrary());
    }

    private static async Task UntilAsync(Func<bool> condition, int seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    // Audible synthetic audio: all-zero PCM is skipped as silence before recognition.
    private static byte[] Tone(int samples)
    {
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2),
                (short)(6000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0)));
        return bytes;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AudioTranscriber-controller-" + Guid.NewGuid().ToString("N"));
        public FakeCapture Capture { get; } = new();
        public FakeMedia Media { get; } = new();
        public FakeProvider Provider { get; }
        public AppController App { get; }
        public ConcurrentQueue<AppNotification> Notifications { get; } = new();
        public Fixture(string provider = "local-whisper")
        {
            Provider = new(provider);
            App = new(Root, Capture, Media, new FakePlayback(), _ => Provider, () => new FakeDiarizer());
            App.Notification += Notifications.Enqueue;
        }
        public void Sql(string commandText)
        {
            using var connection = new SqliteConnection("Data Source=" + Path.Combine(Root, "library.sqlite3"));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.ExecuteNonQuery();
        }
        public async ValueTask DisposeAsync()
        {
            Provider.Release.TrySetResult();
            await App.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FakeCapture : IAudioCaptureService
    {
        private CaptureOptions? options;
        private readonly Guid continuity = Guid.NewGuid();
        private long frames;
        private bool stopped;
        public Guid TrackId => options?.OutputTrackId ?? Guid.Empty;
        public Guid MicrophoneTrackId => options?.MicrophoneTrackId ?? Guid.Empty;
        private readonly Dictionary<Guid, long> trackFrames = new();
        public event Action<NativeChunk>? ChunkSealed;
        public event Action<AudioLevels>? Levels;
        public event Action<CaptureFault>? Fault;
        public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() =>
            [new("synthetic-output", "Synthetic output, no desktop capture", TrackKind.Loopback, true, true)];
        public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() =>
            [new("synthetic-mic", "Synthetic microphone, no desktop capture", TrackKind.Microphone, true, true)];
        public Task<CaptureSession> StartAsync(CaptureOptions request, CancellationToken cancellationToken = default)
        {
            options = request;
            frames = 0;
            stopped = false;
            trackFrames.Clear();
            Directory.CreateDirectory(request.OutputDirectory);
            return Task.FromResult(new CaptureSession(request.SessionId, 0,
                [new(TrackId, request.SessionId, TrackKind.Loopback, "Synthetic", AudioFormat.Pcm16Mono16K)]));
        }
        public void EmitAudio(Guid trackId, float[] samples)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(options!.OutputDirectory, id.ToString("N") + ".synthetic-pcm");
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Clamp(samples[i] * 32768f, -32768f, 32767f));
            File.WriteAllBytes(path, bytes);
            var start = trackFrames.GetValueOrDefault(trackId);
            ChunkSealed?.Invoke(new(id, trackId, path, AudioFormat.Pcm16Mono16K, start, samples.Length,
                AudioTime.FramesToTicks(start, 16000) + options.SessionOffsetTicks, continuity));
            trackFrames[trackId] = start + samples.Length;
            if (trackId == TrackId) frames = start + samples.Length;
        }
        public void Emit(int samples)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(options!.OutputDirectory, id.ToString("N") + ".synthetic-pcm");
            File.WriteAllBytes(path, Tone(samples));
            ChunkSealed?.Invoke(new(id, TrackId, path, AudioFormat.Pcm16Mono16K, frames, samples,
                AudioTime.FramesToTicks(frames, 16000) + options.SessionOffsetTicks, continuity));
            frames += samples;
            Levels?.Invoke(new(TrackId, 0, 0, 0));
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!stopped && options is not null) { stopped = true; Emit(6000); }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { GC.KeepAlive(Fault); return ValueTask.CompletedTask; }
    }

    private sealed class FakeMedia : IMediaNormalizer
    {
        public bool CorruptOutput { get; set; }
        public int CopyCalls { get; private set; }
        public async IAsyncEnumerable<NormalizedChunk> NormalizeAsync(IAsyncEnumerable<NativeChunk> chunks,
            NormalizationOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(options.OutputDirectory);
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                var path = Path.Combine(options.OutputDirectory, chunk.Id.ToString("N") + ".pcm16");
                var bytes = await File.ReadAllBytesAsync(chunk.Path, cancellationToken);
                await File.WriteAllBytesAsync(path, CorruptOutput ? bytes[..^2] : bytes, cancellationToken);
                yield return new(chunk.Id, chunk.TrackId, path, chunk.SourceFrameOffset, chunk.SourceFrameCount,
                    chunk.SourceFrameOffset, chunk.SourceFrameCount, chunk.Format, chunk.SessionStartTicks, chunk.ContinuityId);
            }
        }
        public Task<MediaProbe> ProbeAsync(string localPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaProbe(localPath, new FileInfo(localPath).Length, new FileInfo(localPath).Length / 32000.0,
                [new(0, "synthetic-pcm", AudioFormat.Pcm16Mono16K, 0, null, "1/16000", "en", null)], "synthetic"));
        public async Task<ImportedMedia> CopyImportAsync(MediaImportRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CopyCalls++;
            Directory.CreateDirectory(request.OutputDirectory);
            var path = Path.Combine(request.OutputDirectory, request.TrackId.ToString("N") + ".synthetic-pcm");
            var bytes = await File.ReadAllBytesAsync(request.SourcePath, cancellationToken);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return new(request.TrackId, path, Path.GetFileName(request.SourcePath), Convert.ToHexString(SHA256.HashData(bytes)),
                0, await ProbeAsync(path, cancellationToken), DateTimeOffset.UtcNow);
        }
        public async IAsyncEnumerable<NormalizedChunk> NormalizeImportAsync(ImportedMedia media, NormalizationOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = new FileInfo(media.ManagedOriginalPath).Length / 2;
            yield return new(Guid.NewGuid(), media.TrackId, media.ManagedOriginalPath, 0, samples, 0, samples,
                AudioFormat.Pcm16Mono16K, 0);
            await Task.CompletedTask;
        }
    }

    private sealed class FakeProvider(string id) : ITranscriptionProvider
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool WasCanceled { get; private set; }
        public int SegmentMilliseconds { get; set; } = 100;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<Guid, double> TailDecibels { get; } = new();
        public ProviderDescriptor Descriptor { get; } = new(id, "Synthetic test provider", "no-model",
            id.StartsWith("nvidia-", StringComparison.Ordinal), TimingGranularity.Word);
        public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            request.Validate();
            var audio = await File.ReadAllBytesAsync(request.AudioPath, cancellationToken);
            double energy = 0;
            for (var i = 3 * 16000; i < audio.Length / 2; i++) energy += Math.Pow(BitConverter.ToInt16(audio, i * 2) / 32768.0, 2);
            TailDecibels[request.TrackId] = 10 * Math.Log10(energy / Math.Max(1, audio.Length / 2 - 3 * 16000) + 1e-12);
            Interlocked.Increment(ref calls);
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { WasCanceled = true; throw; }
            var start = request.CoreStartSample * 1000 / 16000 + 100;
            return new(id, "synthetic", TranscriptionStatus.Succeeded,
                [new("hello", start, start + SegmentMilliseconds, TimingGranularity.Word, [new("hello", start, start + SegmentMilliseconds)])],
                "{\"synthetic\":true}");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDiarizer : IDiarizationService, ISpeakerEmbeddingService
    {
        public Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, SpeakerRegistrySnapshot registry,
            CancellationToken cancellationToken = default)
        {
            var end = request.SessionStartTicks + AudioTime.FramesToTicks(request.SampleCount, 16000);
            var speaker = registry.Speakers.FirstOrDefault();
            if (speaker is null)
            {
                var identity = new SpeakerIdentity(Guid.NewGuid(), request.SessionId, 1, "Speaker 1");
                var vector = Enumerable.Range(0, 256).Select(index => index == 0 ? 1f : 0f).ToImmutableArray();
                var embedding = new SpeakerEmbedding(Guid.NewGuid(), request.SessionId, identity.Id,
                    DiarizationModels.EmbeddingSha256, vector, request.SessionStartTicks, end);
                speaker = new(identity, DiarizationModels.EmbeddingSha256, vector, [embedding], end - request.SessionStartTicks);
            }
            return Task.FromResult(new DiarizationResult(
                [new(request.TrackId, request.SessionStartTicks, end, speaker.Identity.Id)],
                new(request.SessionId, registry.Revision + 1, [speaker])));
        }
        // Loud clips are one voice, quiet clips another.
        public async Task<IReadOnlyList<float[]?>> EmbedAsync(string audioPath, long sampleCount, IReadOnlyList<SpeakerEmbeddingClip> clips,
            CancellationToken cancellationToken = default)
        {
            var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
            return clips.Select(clip =>
            {
                double sum = 0;
                for (var i = 0; i < clip.SampleCount; i++)
                    sum += Math.Pow(BitConverter.ToInt16(bytes, (int)(clip.StartSample + i) * 2) / 32768.0, 2);
                var vector = new float[256];
                var rms = Math.Sqrt(sum / clip.SampleCount);
                // A middling level stands for a line whose voice is unclear.
                if (rms is > 0.1 and < 0.2) return null;
                vector[rms > 0.1 ? 0 : 1] = 1;
                return (float[]?)vector;
            }).ToArray();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlayback : IAudioPlaybackService
    {
        public event Action<PlaybackPosition>? PositionChanged;
        public Task PlayAsync(PlaybackRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { GC.KeepAlive(PositionChanged); return ValueTask.CompletedTask; }
    }
}
